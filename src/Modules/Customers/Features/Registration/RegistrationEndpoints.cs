using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>The premise half of an intake body: a new one to register, or one already on the books.</summary>
/// <param name="NewPremise">Address and description of a premise to register with the customer.</param>
/// <param name="ServiceLocationId">A premise already in the registry.</param>
public sealed record IntakePremiseRequest(NewPremiseRequest? NewPremise = null, Guid? ServiceLocationId = null);

/// <summary>A premise being registered as part of an intake.</summary>
/// <param name="Address">Where it is.</param>
/// <param name="Description">What it is, in a crew's words.</param>
public sealed record NewPremiseRequest(AddressPayload Address, string? Description = null);

/// <summary>Body of a customer intake — the wizard's single commit.</summary>
/// <param name="Name">Who they are.</param>
/// <param name="Class">Residential or commercial.</param>
/// <param name="Premise">Where they are to be served.</param>
/// <param name="ContactName">Who to ask for.</param>
/// <param name="Email">Where to email them.</param>
/// <param name="Phone">Where to call them.</param>
/// <param name="DepositCollected">What was taken at the counter. Zero if the deposit was waived.</param>
/// <param name="StartService">Whether supply is energised as part of the intake.</param>
/// <param name="Reason">Why the account was opened, for its history.</param>
public sealed record RegisterCustomerIntakeRequest(
    string Name,
    CustomerClass Class,
    IntakePremiseRequest Premise,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null,
    decimal DepositCollected = 0m,
    bool StartService = false,
    string? Reason = null);

/// <summary>A deposit rule as the API returns it.</summary>
/// <param name="CustomerClass">Which class it applies to.</param>
/// <param name="Amount">What that class is asked for.</param>
/// <param name="Description">Why the figure is what it is.</param>
/// <param name="RuleId">The reference row.</param>
public sealed record DepositRuleResponse(string CustomerClass, decimal Amount, string Description, Guid RuleId)
{
    /// <summary>Projects an assessment for the wire.</summary>
    public static DepositRuleResponse From(DepositAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        return new DepositRuleResponse(
            assessment.CustomerClass.ToString(),
            assessment.Amount,
            assessment.Description,
            assessment.RuleId);
    }
}

/// <summary>What an intake produced, as the API returns it.</summary>
/// <param name="Customer">The customer.</param>
/// <param name="Location">The premise they are served at.</param>
/// <param name="LocationWasRegistered">Whether this intake registered that premise or reused it.</param>
/// <param name="Account">The service account joining the two.</param>
/// <param name="Deposit">What was assessed and what was taken.</param>
public sealed record CustomerRegistrationResponse(
    CustomerResponse Customer,
    ServiceLocationResponse Location,
    bool LocationWasRegistered,
    ServiceAccountResponse Account,
    DepositOutcomeResponse Deposit)
{
    /// <summary>Projects a registration for the wire.</summary>
    public static CustomerRegistrationResponse From(CustomerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return new CustomerRegistrationResponse(
            CustomerResponse.From(registration.Customer),
            ServiceLocationResponse.From(registration.Location),
            registration.LocationWasRegistered,
            ServiceAccountResponse.From(registration.Account),
            new DepositOutcomeResponse(
                registration.Assessment.CustomerClass.ToString(),
                registration.Assessment.Amount,
                registration.DepositCollected,
                registration.Assessment.RuleId));
    }
}

/// <summary>The deposit side of an intake's receipt.</summary>
/// <param name="CustomerClass">The class assessed.</param>
/// <param name="AssessedAmount">What the schedule asked for.</param>
/// <param name="CollectedAmount">What was taken.</param>
/// <param name="RuleId">The reference row the figure came from.</param>
public sealed record DepositOutcomeResponse(string CustomerClass, decimal AssessedAmount, decimal CollectedAmount, Guid RuleId);

/// <summary>Customer intake's HTTP surface.</summary>
public static class RegistrationEndpoints
{
    /// <summary>Route of the intake resource.</summary>
    public const string RoutePrefix = "/api/customer-registrations";

    /// <summary>Route of the deposit schedule.</summary>
    public const string DepositRulesRoute = "/api/deposit-rules";

    /// <summary>Maps the intake endpoints.</summary>
    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // A resource of its own rather than a sub-route of /api/customers: an intake produces a
        // customer, a premise and an account, and hanging it off the customer registry would name it
        // after only the first of the three.
        endpoints
            .MapPost(RoutePrefix, (
                    RegisterCustomerIntakeRequest body,
                    [FromServices] ICustomerRegistrationService intake,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var registration = await intake.RegisterAsync(
                        new CustomerIntakeInput(
                            body.Name,
                            body.Class,
                            new IntakePremise(
                                body.Premise.NewPremise is { } premise
                                    ? new ServiceLocationInput(premise.Address.ToAddress(), premise.Description)
                                    : null,
                                body.Premise.ServiceLocationId),
                            body.ContactName,
                            body.Email,
                            body.Phone,
                            body.DepositCollected,
                            body.StartService,
                            body.Reason),
                        cancellationToken);

                    return Results.Created(
                        $"{CustomerEndpoints.RoutePrefix}/{registration.Customer.Id}",
                        CustomerRegistrationResponse.From(registration));
                }))
            // customers.write opens the door; collecting a deposit needs customers.deposit as well,
            // and the service refuses that inside the request because whether the intake collects one
            // is a fact about the body rather than the route.
            .RequirePermission(Permissions.Customers.Write)
            .WithValidation<RegisterCustomerIntakeRequest>()
            .WithTags("Customers")
            .WithName("RegisterCustomerIntake");

        endpoints
            .MapGet(DepositRulesRoute, async ([FromServices] IDepositRuleService deposits, CancellationToken cancellationToken) =>
                Results.Ok((await deposits.ListAsync(cancellationToken)).Select(DepositRuleResponse.From).ToList()))
            // Read-only, and gated on customers.read rather than customers.deposit: a clerk who may
            // not take a deposit still has to be able to tell a caller what one would cost.
            .RequirePermission(Permissions.Customers.Read)
            .WithTags("Customers")
            .WithName("ListDepositRules");

        return endpoints;
    }
}
