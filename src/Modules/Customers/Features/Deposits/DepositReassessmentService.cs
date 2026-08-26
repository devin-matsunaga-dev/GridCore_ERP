using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>
/// One open service account and what the schedule asks for it today.
/// </summary>
/// <param name="ServiceAccountId">The account assessed.</param>
/// <param name="AccountNumber">Its number, as quoted.</param>
/// <param name="ServiceLocationId">The premise, which is where the usage was measured.</param>
/// <param name="Status">Where the account stands, by name.</param>
/// <param name="Assessment">What the rule for this class and service asks, with the working behind it.</param>
/// <param name="HasUsageHistory">
/// Whether anything was actually measured at the premise. Distinguished from a usage-based rule
/// falling back to its minimum for any other reason, because "we have never read a meter here" is
/// what a rep says to a customer who asks why they are on the floor figure.
/// </param>
public sealed record DepositAccountRequirement(
    Guid ServiceAccountId,
    string AccountNumber,
    Guid ServiceLocationId,
    string Status,
    DepositAssessment Assessment,
    bool HasUsageHistory);

/// <summary>
/// What a customer is holding against what they are now required to hold — the re-assessment
/// WP-2.17 asks for, answered on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>A read and only a read.</b> Nothing here collects, holds, applies or refunds a cent: it says
/// what the schedule would ask of this customer today, and moving money on the strength of that
/// answer is <see cref="ICustomerDepositService"/> and gates on <c>customers.deposit</c>. That split
/// is the reason a re-assessment can be gated on <c>customers.read</c> — quoting a shortfall down
/// the telephone is what a rep does all day, and taking the money is not.
/// </para>
/// <para>
/// <b>Required is a sum over the customer's OPEN accounts, and held is a single balance.</b> The
/// deposit ledger is customer-level (WP-2.12's call) while the schedule is per account, so the two
/// meet here: every open account contributes what its own class-and-service rule asks, and the one
/// balance is set against the total. A closed account contributes nothing — the utility is no longer
/// exposed on a supply it has stopped delivering.
/// </para>
/// </remarks>
/// <param name="CustomerId">Whose deposit.</param>
/// <param name="AccountNumber">The customer number they quote.</param>
/// <param name="CustomerClass">The class every line was assessed on.</param>
/// <param name="Currency">ISO 4217 code the figures are expressed in.</param>
/// <param name="HeldAmount">What the utility is holding — <see cref="Customer.DepositHeld"/>.</param>
/// <param name="RequiredAmount">What the schedule asks across every open account.</param>
/// <param name="ShortfallAmount">What is still to be collected. Floored at zero — never negative.</param>
/// <param name="AssessedAt">The instant the usage behind it was cut off, so the answer is reproducible.</param>
/// <param name="Accounts">The line per open account, in service order.</param>
public sealed record DepositRequirement(
    Guid CustomerId,
    string AccountNumber,
    CustomerClass CustomerClass,
    string Currency,
    decimal HeldAmount,
    decimal RequiredAmount,
    decimal ShortfallAmount,
    DateTimeOffset AssessedAt,
    IReadOnlyList<DepositAccountRequirement> Accounts)
{
    /// <summary>Whether the utility is holding at least what it now asks for.</summary>
    public bool IsCovered => ShortfallAmount <= Money.Zero;
}

/// <summary>What is held against what is required, asked on demand (WP-2.17).</summary>
public interface IDepositReassessmentService
{
    /// <summary>Re-assesses <paramref name="customerId"/> against every open account they hold.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    /// <exception cref="RegistryValidationException">The schedule declares no rule for a pair one of their accounts needs.</exception>
    Task<DepositRequirement> ReassessAsync(Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The re-assessment over the customers schema, the deposit schedule and the usage register.
/// </summary>
/// <remarks>
/// <para>
/// <b>The service obtains its own measured input.</b> Average monthly usage comes from
/// <see cref="IUsageDirectory"/> — Metering's seam, registered by that module — and is never passed
/// in by a caller. A deposit is money asked of a customer, so the figure behind it has to be the
/// authoritative one rather than whatever a screen happened to be holding: a caller that supplied
/// the usage would be a caller that could supply a different number and get a different deposit.
/// </para>
/// <para>
/// <b>One boundary call per premise, not per account.</b> A customer with an electric and a
/// wastewater account at one house asks the usage register once — the wastewater line is unmetered
/// and never asks at all — which keeps a re-assessment a handful of reads however many supplies
/// somebody takes.
/// </para>
/// <para>
/// No unit of work: this writes nothing. It is the only service in this module's deposit slice that
/// does not, which is exactly why it is a separate service rather than a method on the lifecycle.
/// </para>
/// </remarks>
public sealed class DepositReassessmentService(
    CustomersDbContext database,
    IDepositRuleService rules,
    IUsageDirectory usage,
    TimeProvider clock) : IDepositReassessmentService
{
    /// <summary>Most open accounts one re-assessment will consider.</summary>
    /// <remarks>
    /// A premise takes at most one account per service and a customer may hold several premises, so
    /// this is generous rather than tight. It exists so a data fault cannot turn one screen into an
    /// unbounded read, not because anybody is expected to reach it.
    /// </remarks>
    public const int MaxAccounts = 50;

    /// <inheritdoc />
    public async Task<DepositRequirement> ReassessAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        // FindAsync, not a query, for the reason CustomerDepositService.MoveAsync gives: the intake
        // wizard registers a customer, opens their account and collects their deposit inside ONE
        // transaction, and nothing is saved until the outermost unit of work commits. A query here
        // would answer "no such customer" for a customer that is right there in the context.
        var customer = await database.Customers.FindAsync([customerId], cancellationToken).ConfigureAwait(false)
            ?? throw new CustomerNotFoundException(customerId);

        var now = clock.GetUtcNow();

        // Open accounts only — "not Closed" rather than a list of the open statuses, the same
        // predicate ux_service_accounts_open_location filters on. A pending account counts: the
        // deposit is what the utility asks for BEFORE it energises supply, so leaving it out would
        // quote a shortfall of zero to exactly the customer who has not paid one yet.
        var stored = await database.ServiceAccounts
            .AsNoTracking()
            .Where(account => account.CustomerId == customerId)
            .Where(account => account.Status != ServiceAccountStatus.Closed)
            .Take(MaxAccounts)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The change tracker as well as the table, for the reason the customer lookup above uses
        // FindAsync: an account opened moments earlier in this same transaction is not visible to
        // any SQL query until it commits, and at intake that account IS the one being assessed.
        // Local first in the concat and DistinctBy on the id, so a row that is both stored and
        // tracked is counted once.
        var accounts = database.ServiceAccounts.Local
            .Where(account => account.CustomerId == customerId)
            .Where(account => account.Status != ServiceAccountStatus.Closed)
            .Concat(stored)
            .DistinctBy(account => account.Id)
            .OrderBy(account => account.ServiceType)
            .ThenBy(account => account.Id)
            .Take(MaxAccounts)
            .ToList();

        var lines = new List<DepositAccountRequirement>(accounts.Count);
        var byPremise = new Dictionary<(Guid Premise, Contracts.Services.ServiceType Service), PremiseUsage>();

        foreach (var account in accounts)
        {
            var rule = await rules.FindAsync(customer.Class, account.ServiceType, cancellationToken).ConfigureAwait(false)
                ?? throw new RegistryValidationException(
                    DepositRuleService.MissingRule(customer.Class, account.ServiceType));

            var measured = PremiseUsage.None(account.ServiceLocationId);

            // Only a rule that prices usage asks for any. An unmetered service never reaches the
            // register at all, which is what stops a wastewater line making a call whose answer is
            // known in advance.
            if (rule.HasUsageBasis)
            {
                var key = (account.ServiceLocationId, account.ServiceType);

                if (!byPremise.TryGetValue(key, out measured!))
                {
                    measured = await usage
                        .AverageMonthlyAtLocationAsync(
                            account.ServiceLocationId,
                            account.ServiceType,

                            // The rule's own months, not a constant: a deposit worth two months is
                            // averaged over two, and one worth six over six. Asking a fixed window
                            // would make the answer disagree with the rule that quoted it.
                            rule.UsageMonths!.Value,
                            cancellationToken)
                        .ConfigureAwait(false);

                    byPremise[key] = measured;
                }
            }

            lines.Add(new DepositAccountRequirement(
                account.Id,
                account.AccountNumber,
                account.ServiceLocationId,
                account.Status.ToString(),
                DepositAssessment.Of(rule, measured.AverageMonthlyUsage),
                measured.HasHistory));
        }

        var required = Money.Total(lines.Select(line => line.Assessment.Amount));
        var held = customer.DepositHeld;

        return new DepositRequirement(
            customer.Id,
            customer.AccountNumber,
            customer.Class,

            // The currency the schedule is published in. Every line carries one and they are the
            // same one — a multi-currency schedule is not in scope (DepositRules' own note) — so the
            // first line answers, and a customer with no open account falls back to the shipped code.
            lines.Count is 0 ? DepositRules.Currency : lines[0].Assessment.Currency,
            held,
            required,

            // FLOORED AT ZERO, never negative. A customer holding more than the schedule now asks
            // for is not owed a refund by arithmetic — giving a deposit back is a decision somebody
            // makes and a movement WP-2.12 records, and a negative shortfall on a screen would read
            // as the utility announcing one.
            Math.Max(Money.Zero, required - held),
            now,
            lines);
    }
}
