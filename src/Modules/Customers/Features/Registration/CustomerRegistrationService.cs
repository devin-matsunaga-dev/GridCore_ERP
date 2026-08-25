using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
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
/// <param name="Class">Residential or commercial — also what the deposit is assessed from.</param>
/// <param name="Premise">The premise to serve them at, new or existing.</param>
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
/// <b>The deposit is assessed and recorded, and that is all.</b> The figure comes from the schedule
/// in <c>customers.deposit_rules</c>, collecting one is gated on
/// <see cref="Permissions.Customers.Deposit"/> and audited in its own entry — but no journal entry
/// is posted and nothing is held, applied or refunded here. WP-2.12 owns that lifecycle, and this
/// hands off to it rather than growing half of it.
/// </para>
/// </remarks>
public sealed class CustomerRegistrationService(
    ICustomerService customers,
    IServiceLocationService locations,
    IServiceAccountService accounts,
    IDepositRuleService deposits,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
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
                var assessment = await deposits.AssessAsync(input.Class, ct).ConfigureAwait(false);
                var collected = RequireCollectable(input.DepositCollected, assessment);

                // Refused before a single row is written. The check is here rather than on the
                // endpoint because the deposit is one field of a composite intake: the request is
                // permitted or not depending on what is in it, which routing cannot see.
                if (collected > Money.Zero && !currentUser.HasPermission(Permissions.Customers.Deposit))
                {
                    throw new RegistryPermissionException(
                        $"Collecting a deposit requires the '{Permissions.Customers.Deposit}' permission. "
                        + "Register the customer without one and have somebody who holds it take the deposit.");
                }

                var (location, wasRegistered) = await PremiseAsync(input, ct).ConfigureAwait(false);

                var customer = await customers.RegisterAsync(
                    new RegisterCustomerInput(input.Name, input.Class, input.ContactName, input.Email, input.Phone, collected),
                    ct).ConfigureAwait(false);

                if (collected > Money.Zero)
                {
                    // Invariant 5: a sensitive action is permission-gated AND audited. The customer's
                    // own creation entry carries the balance; this one carries what was asked for
                    // beside what was taken, which is the only place the difference is recorded.
                    audit.Record(
                        AuditActions.CustomerDepositCollected,
                        AuditEntityTypes.Customer,
                        customer.Id.ToString(),
                        before: null,
                        after: new DepositCollectionSnapshot(
                            customer.Id,
                            customer.AccountNumber,
                            assessment.CustomerClass,
                            assessment.RuleId,
                            assessment.Amount,
                            collected));
                }

                var account = await accounts.OpenAsync(
                    new OpenServiceAccountInput(customer.Id, location.Id, input.Reason),
                    ct).ConfigureAwait(false);

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
                $"The deposit schedule asks {assessment.Amount:0.00} for a {assessment.CustomerClass} customer, "
                + $"and {collected:0.00} was collected. Collect the assessed amount or less.");
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

/// <summary>
/// The shape a deposit collection is audited as: what the schedule asked for, beside what was
/// taken, and which rule said so.
/// </summary>
/// <param name="CustomerId">Who paid it.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="CustomerClass">The class assessed.</param>
/// <param name="RuleId">The reference row the figure came from.</param>
/// <param name="AssessedAmount">What the schedule asked for.</param>
/// <param name="CollectedAmount">What was actually taken.</param>
public sealed record DepositCollectionSnapshot(
    Guid CustomerId,
    string AccountNumber,
    CustomerClass CustomerClass,
    Guid RuleId,
    decimal AssessedAmount,
    decimal CollectedAmount);
