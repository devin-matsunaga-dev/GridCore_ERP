using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Deposits;

/// <summary>Body of a request to take a deposit.</summary>
/// <param name="Amount">How much was taken. Positive, to the cent.</param>
/// <param name="IsInterestBearing">Whether the holding earns interest. Stored, never accrued in the MVP.</param>
/// <param name="Reason">Why, for the record a rep reads back.</param>
public sealed record CollectDepositRequest(decimal Amount, bool IsInterestBearing = false, string? Reason = null);

/// <summary>Body of a request to put a held deposit against a bill.</summary>
/// <param name="BillId">The bill to settle.</param>
/// <param name="Amount">How much of the deposit to apply. Positive, to the cent.</param>
/// <param name="Reason">Why, for the record a rep reads back.</param>
public sealed record ApplyDepositRequest(Guid BillId, decimal Amount, string? Reason = null);

/// <summary>Body of a request to give a deposit back.</summary>
/// <param name="Amount">How much to return. Positive, to the cent.</param>
/// <param name="Reason">Why, for the record a rep reads back.</param>
public sealed record RefundDepositRequest(decimal Amount, string? Reason = null);

/// <summary>One movement of a customer's deposit, as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="CustomerId">Whose deposit moved.</param>
/// <param name="Kind">Collected, applied or refunded.</param>
/// <param name="Amount">How much moved. Always positive — the kind carries the direction.</param>
/// <param name="SignedAmount">The effect on the balance: the magnitude with its direction applied.</param>
/// <param name="BalanceAfter">What the utility held once this entry was applied.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="IsInterestBearing">The terms a collection was taken under.</param>
/// <param name="BillId">The bill an application settled, if any.</param>
/// <param name="BillNumber">Its number, as printed.</param>
/// <param name="ServiceAccountId">The account that bill was raised against, if any.</param>
/// <param name="Reason">Why, in the operator's words.</param>
/// <param name="ActorId">Subject id of whoever did it.</param>
/// <param name="ActorName">Their display name at the time.</param>
/// <param name="RecordedAt">When it happened.</param>
public sealed record DepositEntryResponse(
    Guid Id,
    Guid CustomerId,
    string Kind,
    decimal Amount,
    decimal SignedAmount,
    decimal BalanceAfter,
    string Currency,
    bool IsInterestBearing,
    Guid? BillId,
    string? BillNumber,
    Guid? ServiceAccountId,
    string? Reason,
    string ActorId,
    string? ActorName,
    DateTimeOffset RecordedAt)
{
    /// <summary>Projects a <see cref="DepositEntry"/> for the wire.</summary>
    public static DepositEntryResponse From(DepositEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new DepositEntryResponse(
            entry.Id,
            entry.CustomerId,
            entry.Kind.ToString(),
            entry.Amount,
            entry.SignedAmount,
            entry.BalanceAfter,
            entry.Currency,
            entry.IsInterestBearing,
            entry.BillId,
            entry.BillNumber,
            entry.ServiceAccountId,
            entry.Reason,
            entry.ActorId,
            entry.ActorName,
            entry.RecordedAt);
    }
}

/// <summary>One customer's deposit, as the API returns it.</summary>
/// <param name="CustomerId">Whose deposit.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="Balance">What the utility holds. These entries add up to it.</param>
/// <param name="Currency">ISO 4217 code the balance is expressed in.</param>
/// <param name="CustomerClass">The class the schedule was read for.</param>
/// <param name="AssessedAmount">What the schedule asks of a customer of that class.</param>
/// <param name="ShortfallAmount">How much less than the assessed figure is held, or zero when it is covered.</param>
/// <param name="RuleId">The reference row the assessed figure came from.</param>
/// <param name="IsInterestBearing">The terms the money now held was taken under.</param>
/// <param name="Entries">Every movement, newest first.</param>
public sealed record DepositLedgerResponse(
    Guid CustomerId,
    string AccountNumber,
    decimal Balance,
    string Currency,
    string CustomerClass,
    decimal AssessedAmount,
    decimal ShortfallAmount,
    Guid RuleId,
    bool IsInterestBearing,
    IReadOnlyList<DepositEntryResponse> Entries)
{
    /// <summary>Projects a <see cref="DepositLedger"/> for the wire.</summary>
    /// <remarks>
    /// The shortfall is computed here rather than in the browser, and it is <b>floored at zero</b>:
    /// a customer holding more than the schedule asks for is not short by a negative amount, and a
    /// screen handed one would have to decide what a negative shortfall means.
    /// </remarks>
    public static DepositLedgerResponse From(DepositLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        return new DepositLedgerResponse(
            ledger.CustomerId,
            ledger.AccountNumber,
            ledger.Balance,
            ledger.Currency,
            ledger.Assessment.CustomerClass.ToString(),
            ledger.Assessment.Amount,
            Math.Max(0m, ledger.Assessment.Amount - ledger.Balance),
            ledger.Assessment.RuleId,
            ledger.IsInterestBearing,
            [.. ledger.Entries.Select(DepositEntryResponse.From)]);
    }
}

/// <summary>
/// The deposit lifecycle's HTTP surface: one ledger to read, and three ways to move it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writes demand <see cref="Permissions.Customers.Deposit"/> at the route</b>, unlike WP-2.8's
/// intake. That is not a change of mind: the intake takes a deposit as one optional field of a
/// composite request, so the route could not tell whether money was involved and the gate had to be
/// in the service. Every request here <i>is</i> a deposit movement, so the route can say so — and the
/// service demands it too, because the intake reaches <c>CollectAsync</c> in process.
/// </para>
/// <para>
/// <b>The read is gated on <see cref="Permissions.Customers.Read"/>, deliberately.</b> A clerk who
/// may not take money still has to be able to tell a caller what the utility is holding — the same
/// call WP-2.8 made about the deposit schedule.
/// </para>
/// <para>
/// Three POST sub-resources rather than a PUT of a balance, per CONVENTIONS.md: these are
/// non-CRUD acts, and the balance is not a field anybody sets.
/// </para>
/// </remarks>
public static class DepositEndpoints
{
    /// <summary>Route of one customer's deposit ledger.</summary>
    public const string RoutePrefix = "/api/customers/{customerId:guid}/deposits";

    /// <summary>Maps the deposit endpoints.</summary>
    public static IEndpointRouteBuilder MapDepositEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Customers");

        group
            .MapGet("/", ([FromRoute] Guid customerId, [FromServices] ICustomerDepositService deposits, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                    Results.Ok(DepositLedgerResponse.From(await deposits.GetAsync(customerId, cancellationToken)))))
            .RequirePermission(Permissions.Customers.Read)
            .WithName("GetCustomerDeposits");

        group
            .MapPost("/collections", ([FromRoute] Guid customerId, CollectDepositRequest body, [FromServices] ICustomerDepositService deposits, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var entry = await deposits.CollectAsync(
                        customerId,
                        new CollectDepositInput(body.Amount, body.IsInterestBearing, body.Reason),
                        cancellationToken);

                    return Results.Created($"/api/customers/{customerId}/deposits", DepositEntryResponse.From(entry));
                }))
            .RequirePermission(Permissions.Customers.Deposit)
            .WithValidation<CollectDepositRequest>()
            .WithName("CollectCustomerDeposit");

        group
            .MapPost("/applications", ([FromRoute] Guid customerId, ApplyDepositRequest body, [FromServices] ICustomerDepositService deposits, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var entry = await deposits.ApplyAsync(
                        customerId,
                        new ApplyDepositInput(body.BillId, body.Amount, body.Reason),
                        cancellationToken);

                    return Results.Created($"/api/customers/{customerId}/deposits", DepositEntryResponse.From(entry));
                }))
            .RequirePermission(Permissions.Customers.Deposit)
            .WithValidation<ApplyDepositRequest>()
            .WithName("ApplyCustomerDeposit");

        group
            .MapPost("/refunds", ([FromRoute] Guid customerId, RefundDepositRequest body, [FromServices] ICustomerDepositService deposits, CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var entry = await deposits.RefundAsync(
                        customerId,
                        new RefundDepositInput(body.Amount, body.Reason),
                        cancellationToken);

                    return Results.Created($"/api/customers/{customerId}/deposits", DepositEntryResponse.From(entry));
                }))
            .RequirePermission(Permissions.Customers.Deposit)
            .WithValidation<RefundDepositRequest>()
            .WithName("RefundCustomerDeposit");

        return endpoints;
    }
}
