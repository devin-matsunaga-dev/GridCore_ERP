using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;
using GridCore.Platform.Security;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>
/// The premise a new customer is to be served at: either one to register, or one already on the
/// books. Exactly one of the two, which <see cref="CustomerIntakeInput.RequireOnePremise"/> enforces.
/// </summary>
/// <param name="NewPremise">A premise to register as part of the intake.</param>
/// <param name="ServiceLocationId">A premise already registered, picked from the registry.</param>
public sealed record IntakePremise(ServiceLocationInput? NewPremise = null, Guid? ServiceLocationId = null);

/// <summary>
/// One customer's intake, as one act: who they are, where they are served, whether supply is
/// energised on the spot, and what deposit was taken.
/// </summary>
/// <param name="Name">Who they are.</param>
/// <param name="Class">Residential or commercial — half of what the deposit is assessed from.</param>
/// <param name="Premise">The premise to serve them at, new or existing.</param>
/// <param name="ServiceType">
/// Which supply they are applying for (WP-2.17) — the other half of the deposit key, and what the
/// account will declare. Electricity by default, because that is the one service the demonstration
/// utility distributes and an intake wizard that made a rep choose it every time would be a wizard
/// asking a question with one real answer.
/// </param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <param name="DepositCollected">
/// What was actually taken. Zero means none was — a deposit may be waived, and a waived one needs
/// no permission because no money changed hands.
/// </param>
/// <param name="StartService">Whether supply is energised as part of the intake.</param>
/// <param name="Reason">Why the account was opened, for its history.</param>
public sealed record CustomerIntakeInput(
    string Name,
    CustomerClass Class,
    IntakePremise Premise,
    ServiceType ServiceType = ServiceType.Electricity,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null,
    decimal DepositCollected = 0m,
    bool StartService = false,
    string? Reason = null)
{
    /// <summary>
    /// Fails unless exactly one premise was named. Neither is a form that was never finished; both
    /// is two premises with no way to say which one the account belongs at.
    /// </summary>
    /// <exception cref="RegistryValidationException">Neither or both were given.</exception>
    public void RequireOnePremise()
    {
        var isNew = Premise?.NewPremise is not null;
        var isExisting = Premise?.ServiceLocationId is { } id && id != Guid.Empty;

        if (isNew == isExisting)
        {
            throw new RegistryValidationException(
                isNew
                    ? "An intake names either a new premise or an existing one, not both."
                    : "An intake needs a premise: register a new one or pick one from the registry.");
        }
    }
}

/// <summary>What one intake produced.</summary>
/// <param name="Customer">The customer, as registered.</param>
/// <param name="Location">The premise they are served at — newly registered, or the one that was picked.</param>
/// <param name="LocationWasRegistered">Whether this intake registered that premise or reused it.</param>
/// <param name="Account">The service account joining the two, energised if the intake asked for it.</param>
/// <param name="Assessment">What the schedule asked for.</param>
/// <param name="DepositCollected">What was taken.</param>
public sealed record CustomerRegistration(
    Customer Customer,
    ServiceLocation Location,
    bool LocationWasRegistered,
    ServiceAccount Account,
    DepositAssessment Assessment,
    decimal DepositCollected);

/// <summary>Customer intake: the guided onboarding flow, as one call.</summary>
public interface ICustomerRegistrationService
{
    /// <summary>Registers a customer, their premise and their service account in one transaction.</summary>
    /// <exception cref="RegistryValidationException">The intake is incomplete or the deposit is not a figure GridCore accepts.</exception>
    /// <exception cref="RegistryPermissionException">A deposit was collected by a caller who may not take one.</exception>
    /// <exception cref="RegistryWorkflowException">The premise is deactivated, already served, or a number was taken mid-flight.</exception>
    /// <exception cref="ServiceLocationNotFoundException">The premise picked does not exist.</exception>
    Task<CustomerRegistration> RegisterAsync(CustomerIntakeInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// The intake wizard's one commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>This composes the three registries rather than reimplementing them.</b> It calls
/// <see cref="ICustomerService"/>, <see cref="IServiceLocationService"/> and
/// <see cref="IServiceAccountService"/> inside a single <see cref="IUnitOfWork.ExecuteAsync"/>, and
/// each of those opens a unit of work of its own that — being nested — joins this one instead of
/// committing. So every guard, audit entry and event those registries already own applies here
/// unchanged, and all of it lands in one transaction: an intake abandoned or refused at any point
/// leaves no customer, no premise and no account behind.
/// </para>
/// <para>
/// <b>The deposit is assessed here and collected by the lifecycle that owns it.</b> The figure comes
/// from the schedule in <c>customers.deposit_rules</c> and the ceiling below is the intake's own
/// rule; everything after that — the <see cref="Permissions.Customers.Deposit"/> gate, the ledger
/// entry, the audit trail and the event Finance posts the liability from — belongs to
/// <see cref="ICustomerDepositService"/>. WORK_PACKAGES.md asked WP-2.8 to "hand off to the WP-2.12
/// lifecycle rather than duplicating it", and WP-2.12 is what made that possible: this used to hold
/// its own copy of the permission check and the audit entry, and now holds neither.
/// </para>
/// <para>
/// The collection runs <i>inside</i> this transaction, against a customer that has been added but
/// not yet saved. That works because the deposit service finds the customer through the change
/// tracker — see its <c>MoveAsync</c> — and it is what keeps an abandoned or refused intake from
/// leaving a deposit entry behind.
/// </para>
/// </remarks>
public sealed class CustomerRegistrationService(
    ICustomerService customers,
    IServiceLocationService locations,
    IServiceAccountService accounts,
    IDepositRuleService deposits,
    ICustomerDepositService depositLedger,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser) : ICustomerRegistrationService
{
    /// <inheritdoc />
    public Task<CustomerRegistration> RegisterAsync(CustomerIntakeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        input.RequireOnePremise();

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                // The schedule figure for the pair being applied for, and deliberately not a
                // usage-based re-assessment: an intake has no premise history to average, and the
                // premise may not even exist until this transaction commits. WP-2.17's re-assessment
                // is what an established customer is asked, later, once there is something to measure.
                var assessment = await deposits.AssessAsync(input.Class, input.ServiceType, ct).ConfigureAwait(false);
                var collected = RequireCollectable(input.DepositCollected, assessment);

                // Refused before a single row is written, and still refused by the ledger a few
                // lines below. The check is duplicated here on purpose: a caller who may not take a
                // deposit should be told before a premise and a customer have been registered, not
                // after — the intake is one transaction, so either way nothing survives, but the
                // 403 is cheaper and the message is about the deposit rather than about the last
                // step to run.
                if (collected > Money.Zero && !currentUser.HasPermission(Permissions.Customers.Deposit))
                {
                    throw new RegistryPermissionException(
                        $"Collecting a deposit requires the '{Permissions.Customers.Deposit}' permission. "
                        + "Register the customer without one and have somebody who holds it take the deposit.");
                }

                var (location, wasRegistered) = await PremiseAsync(input, ct).ConfigureAwait(false);

                var customer = await customers.RegisterAsync(
                    new RegisterCustomerInput(input.Name, input.Class, input.ContactName, input.Email, input.Phone),
                    ct).ConfigureAwait(false);

                var account = await accounts.OpenAsync(
                    new OpenServiceAccountInput(customer.Id, location.Id, input.ServiceType, input.Reason),
                    ct).ConfigureAwait(false);

                if (collected > Money.Zero)
                {
                    // AFTER the account is opened since WP-2.17, and the order is load-bearing. A
                    // deposit is assessed against the supplies a customer takes, so the ledger's own
                    // audit entry — which records what was asked for beside what was taken — has
                    // nothing to ask about until the account exists. Collecting first would have
                    // audited every intake deposit as "assessed: nothing".
                    //
                    // The lifecycle's act, not a copy of it: one ledger entry, one audit entry, and
                    // one event for Finance to post the liability from. A waived deposit does none of
                    // that, because nothing changed hands — which is also why it needs no permission.
                    await depositLedger.CollectAsync(
                        customer.Id,
                        new CollectDepositInput(collected, Reason: input.Reason ?? "Collected at intake."),
                        ct).ConfigureAwait(false);
                }

                if (input.StartService)
                {
                    // Energising is its own act with its own history line and its own event, exactly
                    // as it is from the account screen — the intake presses the same button.
                    account = await accounts.StartServiceAsync(account.Id, input.Reason, ct).ConfigureAwait(false);
                }

                return new CustomerRegistration(customer, location, wasRegistered, account, assessment, collected);
            },
            cancellationToken);
    }

    /// <summary>
    /// The amount that may be taken against <paramref name="assessment"/>.
    /// </summary>
    /// <remarks>
    /// Less than the assessed figure is allowed: a part-payment at the counter is ordinary, and the
    /// balance is a receivable WP-2.12 will track. More than it is refused rather than accepted,
    /// because the schedule is the reason a deposit is owed at all — taking more needs a rule that
    /// says so, and inventing one at the keyboard would put a figure on a customer's record that no
    /// reference row explains.
    /// </remarks>
    private static decimal RequireCollectable(decimal collected, DepositAssessment assessment)
    {
        if (collected < Money.Zero)
        {
            throw new RegistryValidationException("A deposit collected cannot be negative; money owed back is WP-2.12's refund, not a negative deposit.");
        }

        // Refused, not rounded: this is a figure somebody typed at a counter, and the column's
        // scale would silently truncate it (the call WP-1.1 made, and Money.IsRounded is how to ask).
        if (!Money.IsRounded(collected))
        {
            throw new RegistryValidationException($"A deposit must be a whole number of cents; '{collected}' is not.");
        }

        return collected <= assessment.Amount
            ? collected
            : throw new RegistryValidationException(
                $"The deposit schedule asks {assessment.Amount:0.00} for a {assessment.CustomerClass} customer "
                + $"taking {assessment.ServiceType}, and {collected:0.00} was collected. Collect the assessed amount or less.");
    }

    private async Task<(ServiceLocation Location, bool WasRegistered)> PremiseAsync(CustomerIntakeInput input, CancellationToken cancellationToken)
    {
        if (input.Premise.NewPremise is { } premise)
        {
            return (await locations.RegisterAsync(premise, cancellationToken).ConfigureAwait(false), true);
        }

        var id = input.Premise.ServiceLocationId!.Value;

        // Whether it may be served — deactivated, already taken — is the account registry's
        // question and it asks it a few lines later. This only has to find the row.
        var existing = await locations.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceLocationNotFoundException(id);

        return (existing, false);
    }
}

