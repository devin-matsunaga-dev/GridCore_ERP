using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>What a caller supplies to raise a fee against a service account.</summary>
/// <param name="ServiceAccountId">The account to charge.</param>
/// <param name="Code">Which published fee.</param>
/// <param name="Reason">Why. Required — this is the sensitive action invariant 5 is about.</param>
/// <param name="RaisedOn">
/// The day to price against. Defaults to today. Its own field because a fee raised today for
/// something done last week is priced on last week's schedule.
/// </param>
/// <param name="BasisAmount">
/// What to take a <see cref="FeeBasis.Rate"/> fee's rate on — the past-due balance, for a late
/// charge (WP-2.19). Required for a rate fee and refused on a flat one.
/// </param>
/// <remarks>
/// <b><see cref="BasisAmount"/> is deliberately not on <c>RaiseChargeRequest</c>.</b> A rate fee's
/// basis is a figure the register computes, exactly as its rate is a figure the schedule publishes,
/// and a field a rep could type would be a rep inventing the balance a customer is charged on — the
/// same argument WP-2.16 made for having no amount field. The only caller that supplies one is
/// <c>LateChargeService</c>, in process.
/// </remarks>
public sealed record RaiseChargeInput(
    Guid ServiceAccountId,
    FeeCode Code,
    string Reason,
    DateOnly? RaisedOn = null,
    decimal? BasisAmount = null);

/// <summary>What a caller supplies to withdraw a raised charge.</summary>
/// <param name="Reason">Why. Required — it removes money the utility was going to be owed.</param>
public sealed record CancelChargeInput(string Reason);

/// <summary>What a caller supplies to bill a charge at the counter.</summary>
/// <param name="Reason">What to record against the bill's issue.</param>
public sealed record BillChargeInput(string? Reason = null);

/// <summary>How the charge register is filtered.</summary>
/// <param name="ServiceAccountId">Only charges raised against this account.</param>
/// <param name="CustomerId">Only charges owed by this customer.</param>
/// <param name="Status">Only charges in this status.</param>
/// <param name="PendingOnly">Only charges still waiting for a bill, without naming the status.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record AccountChargeQuery(
    Guid? ServiceAccountId = null,
    Guid? CustomerId = null,
    AccountChargeStatus? Status = null,
    bool? PendingOnly = null,
    int Limit = 50);

/// <summary>What billing a charge at the counter produced.</summary>
/// <param name="Charge">The charge, now <see cref="AccountChargeStatus.Billed"/>.</param>
/// <param name="Bill">The bill it was put on — raised and issued in the same act.</param>
public sealed record CounterBillResult(AccountCharge Charge, Bill Bill);

/// <summary>Fees raised against service accounts.</summary>
public interface IAccountChargeService
{
    /// <summary>
    /// Raises a published fee against an account, priced by the schedule in force on the day.
    /// </summary>
    /// <exception cref="BillingPermissionException">The caller may not charge fees.</exception>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    /// <exception cref="BillingValidationException">
    /// The fee is not one GridCore declares, the schedule publishes no figure for it on that day, or
    /// no reason was given.
    /// </exception>
    Task<AccountCharge> RaiseAsync(RaiseChargeInput input, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a charge that has not reached a bill.</summary>
    /// <exception cref="BillingPermissionException">The caller may not charge fees.</exception>
    /// <exception cref="AccountChargeNotFoundException">There is no such charge.</exception>
    /// <exception cref="BillingWorkflowException">It has already been billed, or already withdrawn.</exception>
    Task<AccountCharge> CancelAsync(Guid chargeId, CancelChargeInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a pending charge on a bill of its own and issues it, so the customer can pay it now.
    /// </summary>
    /// <exception cref="BillingPermissionException">The caller may not charge fees.</exception>
    /// <exception cref="AccountChargeNotFoundException">There is no such charge.</exception>
    /// <exception cref="BillingWorkflowException">It has already been billed, or was withdrawn.</exception>
    Task<CounterBillResult> BillNowAsync(Guid chargeId, BillChargeInput input, CancellationToken cancellationToken = default);

    /// <summary>The charge register, newest first.</summary>
    Task<IReadOnlyList<AccountCharge>> ListAsync(AccountChargeQuery query, CancellationToken cancellationToken = default);

    /// <summary>One charge, or <see langword="null"/> when there is no such id.</summary>
    Task<AccountCharge?> FindAsync(Guid chargeId, CancellationToken cancellationToken = default);
}

/// <summary>Fees raised against service accounts, over the billing schema.</summary>
/// <remarks>
/// <para>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so a charge, its audit entry and — where it is billed at the counter —
/// the bill and its <see cref="BillIssued"/> outbox row are one transaction (invariants 1 and 2).
/// </para>
/// <para>
/// <b>The permission is demanded here as well as on the route.</b> The shape WP-2.12's deposit and
/// WP-2.15's transitions settled on rather than WP-2.11's: every write in this slice <i>is</i> a
/// charge, so the route can honestly carry the gate — and the service demands it too because it is
/// reachable in process. WP-2.19's late-charge run and WP-2.22's returned-payment fee will call
/// <see cref="RaiseAsync"/> and not a URL.
/// </para>
/// <para>
/// It reads nothing outside its own schema: accounts arrive through
/// <see cref="IServiceAccountDirectory"/>, the interface in <c>Contracts</c> that Customers
/// registers.
/// </para>
/// </remarks>
public sealed class AccountChargeService(
    BillingDbContext database,
    IServiceAccountDirectory accounts,
    IFeeScheduleService schedule,
    IBillNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IAccountChargeService
{
    /// <summary>The largest page a list will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<AccountCharge> RaiseAsync(RaiseChargeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireChargePermission();

                var now = clock.GetUtcNow();
                var raisedOn = input.RaisedOn ?? DateOnly.FromDateTime(now.UtcDateTime);

                var account = await accounts.FindAsync(input.ServiceAccountId, ct).ConfigureAwait(false)
                    ?? throw new ServiceAccountNotFoundException(input.ServiceAccountId);

                // THE EFFECTIVE DATING, and the reason a reprint after a repricing still shows the
                // old figure: the charge is priced once, here, and stamps the row that priced it.
                // Nothing downstream ever asks the catalogue again.
                var assessment = await schedule.AssessAsync(input.Code, raisedOn, ct).ConfigureAwait(false);

                // WP-2.19's rate basis. A rate fee arrives unpriced and is priced here, on a figure
                // the caller computed; a flat fee arrives priced and refuses a basis, because a
                // caller that supplied one was expecting arithmetic that is not going to happen.
                assessment = (assessment.Basis, input.BasisAmount) switch
                {
                    (FeeBasis.Rate, { } basis) => assessment.PriceOn(basis),
                    (FeeBasis.Rate, null) => throw new BillingValidationException(
                        $"{input.Code} is charged as a rate on a balance, so raising one needs the balance to charge on."),
                    (FeeBasis.Flat, not null) => throw new BillingValidationException(
                        $"{input.Code} is published as a flat fee; there is nothing for a basis of "
                        + $"{input.BasisAmount:0.00} to change about its figure."),
                    _ => assessment,
                };

                var charge = AccountCharge.Raise(assessment, account, raisedOn, input.Reason, RegistryActor.Of(currentUser), now);

                database.AccountCharges.Add(charge);

                // INVARIANT 5. Money the customer will be asked for, so the entry carries the
                // schedule row that priced it as well as the figure — which together are what let
                // somebody answer "why is this $135" after the schedule has moved on.
                audit.Record(
                    AuditActions.AccountChargeRaised,
                    AuditEntityTypes.AccountCharge,
                    charge.Id.ToString(),
                    before: null,
                    after: AccountChargeSnapshot.Of(charge));

                // No event. Nothing downstream prices off a raised charge, and the receivable is
                // raised by BillIssued when the fee reaches a bill — the same line WP-2.15 drew
                // about a status change: an event nobody consumes is an instruction rather than a
                // fact. WP-2.19 and WP-2.22 raise charges in process, not over the bus.
                return charge;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AccountCharge> CancelAsync(Guid chargeId, CancelChargeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireChargePermission();

                var now = clock.GetUtcNow();

                var charge = await LoadAsync(chargeId, ct).ConfigureAwait(false);
                var before = AccountChargeSnapshot.Of(charge);

                charge.Cancel(input.Reason, now);

                audit.Record(
                    AuditActions.AccountChargeCancelled,
                    AuditEntityTypes.AccountCharge,
                    charge.Id.ToString(),
                    before,
                    AccountChargeSnapshot.Of(charge));

                return charge;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<CounterBillResult> BillNowAsync(Guid chargeId, BillChargeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                RequireChargePermission();

                var now = clock.GetUtcNow();
                var today = DateOnly.FromDateTime(now.UtcDateTime);

                var charge = await LoadAsync(chargeId, ct).ConfigureAwait(false);
                var before = AccountChargeSnapshot.Of(charge);

                // The account is re-read rather than taken off the charge: the bill is a document
                // going to whoever holds the account NOW, and the charge stamped the name it was
                // raised under, which may be a tenant who has since moved out.
                var account = await accounts.FindAsync(charge.ServiceAccountId, ct).ConfigureAwait(false)
                    ?? throw new ServiceAccountNotFoundException(charge.ServiceAccountId);

                var actor = RegistryActor.Of(currentUser);

                var bill = Bill.ForCharges(
                    await numbers.NextBillNumberAsync(ct).ConfigureAwait(false),
                    account,
                    [charge.AsBillLine()],
                    charge.Currency,
                    today,
                    actor,
                    now);

                // ISSUED IN THE SAME ACT, deliberately. A charge bill exists because the customer is
                // paying now; leaving it a draft would mean a second permission (billing.generate,
                // which the front desk does not hold) and a second screen before they could. The
                // ordinary term still applies — somebody who says they will pay on Friday is not put
                // in arrears by having asked for the bill on Tuesday.
                bill.Issue(today, today.AddDays(BillingTerms.DueDays), actor, now, input.Reason);

                database.Bills.Add(bill);

                charge.MarkBilled(bill.Id, bill.BillNumber, now);

                // TWO entries, because two things happened: a charge moved, and a bill was issued.
                // The bill's is the one every other issue in this module writes, and leaving it out
                // would make "who issued this bill" answerable for a cycle bill and not for a
                // counter one.
                audit.Record(
                    AuditActions.AccountChargeBilled,
                    AuditEntityTypes.AccountCharge,
                    charge.Id.ToString(),
                    before,
                    AccountChargeSnapshot.Of(charge));

                audit.Record(
                    AuditActions.BillIssued,
                    AuditEntityTypes.Bill,
                    bill.Id.ToString(),
                    before: null,
                    after: BillSnapshot.Of(bill));

                // The receivable. Finance credits fee revenue rather than utility revenue for the
                // fee half of a bill, and on a charge bill that is the whole of it.
                await events.PublishAsync(
                        BillIssued.For(
                            now,
                            bill.Id,
                            bill.BillNumber,
                            bill.ServiceAccountId,
                            bill.CustomerId,
                            bill.PeriodStart,
                            bill.PeriodEnd,
                            bill.DueDate!.Value,
                            bill.TotalAmount,
                            bill.Currency,
                            bill.FeeAmount),
                        ct)
                    .ConfigureAwait(false);

                return new CounterBillResult(charge, bill);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountCharge>> ListAsync(
        AccountChargeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var charges = database.AccountCharges.AsNoTracking();

        if (query.ServiceAccountId is { } serviceAccountId)
        {
            charges = charges.Where(charge => charge.ServiceAccountId == serviceAccountId);
        }

        if (query.CustomerId is { } customerId)
        {
            charges = charges.Where(charge => charge.CustomerId == customerId);
        }

        if (query.Status is { } status)
        {
            charges = charges.Where(charge => charge.Status == status);
        }

        // Named rather than left to the caller to spell, the shape BillQuery.OutstandingOnly has:
        // "what is waiting to be billed" is the question the desk and the billing run both ask.
        if (query.PendingOnly is true)
        {
            charges = charges.Where(charge => charge.Status == AccountChargeStatus.Pending);
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await charges
            .OrderByDescending(charge => charge.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AccountCharge?> FindAsync(Guid chargeId, CancellationToken cancellationToken = default) =>
        database.AccountCharges.AsNoTracking().FirstOrDefaultAsync(charge => charge.Id == chargeId, cancellationToken);

    /// <summary>
    /// Refuses a caller who may not charge fees.
    /// </summary>
    /// <remarks>
    /// CONVENTIONS.md's rule that a service enforces its own permissions rather than trusting the
    /// route that called it. The route carries the same gate, so a request is refused before the
    /// handler runs; this is what refuses an in-process caller.
    /// </remarks>
    /// <exception cref="BillingPermissionException">The caller does not hold <c>billing.charge</c>.</exception>
    private void RequireChargePermission()
    {
        if (!currentUser.HasPermission(Permissions.Billing.Charge))
        {
            throw new BillingPermissionException(
                $"Raising a fee against an account needs '{Permissions.Billing.Charge}'.");
        }
    }

    /// <summary>One charge, tracked, for a write.</summary>
    private async Task<AccountCharge> LoadAsync(Guid chargeId, CancellationToken cancellationToken) =>
        await database.AccountCharges
            .FirstOrDefaultAsync(charge => charge.Id == chargeId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new AccountChargeNotFoundException(chargeId);
}

/// <summary>
/// The shape a charge is audited as. A dedicated record rather than the entity, so changing the
/// entity later cannot silently change the meaning of historic entries.
/// </summary>
/// <param name="Id">Which charge.</param>
/// <param name="ServiceAccountId">The account charged.</param>
/// <param name="AccountNumber">Its number, so the entry is readable without a second lookup.</param>
/// <param name="Code">Which published fee.</param>
/// <param name="Description">What the line will say on the bill.</param>
/// <param name="Basis">Whether the schedule published an amount or a rate.</param>
/// <param name="Rate">The rate it was taken at, on a rate fee.</param>
/// <param name="BasisAmount">What that rate was taken on.</param>
/// <param name="Amount">What was charged.</param>
/// <param name="Currency">ISO 4217 code it is expressed in.</param>
/// <param name="FeeScheduleId">The schedule row that priced it — how a figure is traced.</param>
/// <param name="ScheduleEffectiveFrom">The day that figure took effect.</param>
/// <param name="RaisedOn">The day priced against.</param>
/// <param name="Status">Where the charge stands.</param>
/// <param name="BillNumber">The bill it landed on, once it has.</param>
/// <param name="Reason">Why it was raised.</param>
public sealed record AccountChargeSnapshot(
    Guid Id,
    Guid ServiceAccountId,
    string AccountNumber,
    FeeCode Code,
    string Description,
    FeeBasis Basis,
    decimal? Rate,
    decimal? BasisAmount,
    decimal Amount,
    string Currency,
    Guid FeeScheduleId,
    DateOnly ScheduleEffectiveFrom,
    DateOnly RaisedOn,
    AccountChargeStatus Status,
    string? BillNumber,
    string Reason)
{
    /// <summary>Takes a snapshot of <paramref name="charge"/> as it stands.</summary>
    public static AccountChargeSnapshot Of(AccountCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);

        return new AccountChargeSnapshot(
            charge.Id,
            charge.ServiceAccountId,
            charge.AccountNumber,
            charge.Code,
            charge.Description,
            charge.Basis,
            charge.Rate,
            charge.BasisAmount,
            charge.Amount,
            charge.Currency,
            charge.FeeScheduleId,
            charge.ScheduleEffectiveFrom,
            charge.RaisedOn,
            charge.Status,
            charge.BillNumber,
            charge.Reason);
    }
}
