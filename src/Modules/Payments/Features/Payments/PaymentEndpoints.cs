using GridCore.Modules.Payments.Features.Shared;
using GridCore.Platform.Security;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>Body of a request to take a payment against a bill.</summary>
/// <param name="BillId">The bill being settled.</param>
/// <param name="Amount">How much, always positive and exact to the cent.</param>
/// <param name="Method">How it is being paid — <c>card</c>, <c>bank-transfer</c> or <c>cash</c>.</param>
/// <param name="Instrument">
/// The instrument charged, as the utility is allowed to hold it — a masked card tail or a mandate
/// reference. Ignored for cash. <b>Never a full card number:</b> GridCore does not take one.
/// </param>
public sealed record TakePaymentRequest(Guid BillId, decimal Amount, string Method, string? Instrument = null);

/// <summary>A payment as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="PaymentNumber">The number on the receipt.</param>
/// <param name="ServiceAccountId">The account credited.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="CustomerId">Who paid.</param>
/// <param name="CustomerName">Their name at the time.</param>
/// <param name="BillId">The bill settled.</param>
/// <param name="BillNumber">Its number.</param>
/// <param name="Amount">How much was asked for.</param>
/// <param name="Currency">ISO 4217 code it is expressed in.</param>
/// <param name="Method">How it was paid.</param>
/// <param name="Instrument">The instrument charged, masked.</param>
/// <param name="BalanceBefore">What was owed on the bill when the payment was taken.</param>
/// <param name="Status">Where the attempt stands.</param>
/// <param name="Outcome">What the provider answered, by name.</param>
/// <param name="AllowedTransitions">The statuses it may move to, for rendering buttons.</param>
/// <param name="IsSettled">Whether the utility actually holds this money.</param>
/// <param name="ProviderName">What answered.</param>
/// <param name="ProviderReference">Its reference, for reconciliation.</param>
/// <param name="ProviderMessage">What it said about the attempt.</param>
/// <param name="RequestedAt">When the payment was taken.</param>
/// <param name="SettledAt">When the provider answered.</param>
/// <param name="StatusReason">Why the status last moved.</param>
/// <param name="ActorId">Subject id of whoever took it.</param>
/// <param name="ActorName">Their name at the time.</param>
public sealed record PaymentResponse(
    Guid Id,
    string PaymentNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid CustomerId,
    string CustomerName,
    Guid BillId,
    string BillNumber,
    decimal Amount,
    string Currency,
    string Method,
    string? Instrument,
    decimal BalanceBefore,
    string Status,
    string? Outcome,
    IReadOnlyList<string> AllowedTransitions,
    bool IsSettled,
    string? ProviderName,
    string? ProviderReference,
    string? ProviderMessage,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SettledAt,
    string? StatusReason,
    string ActorId,
    string? ActorName)
{
    /// <summary>Projects a payment for the wire.</summary>
    public static PaymentResponse From(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new PaymentResponse(
            payment.Id,
            payment.PaymentNumber,
            payment.ServiceAccountId,
            payment.AccountNumber,
            payment.CustomerId,
            payment.CustomerName,
            payment.BillId,
            payment.BillNumber,
            payment.Amount,
            payment.Currency,
            payment.Method,
            payment.Instrument,
            payment.BalanceBefore,
            payment.Status.ToString(),
            payment.Outcome?.ToString(),

            // By name, so a UI renders buttons from what the state machine actually allows rather
            // than from a list it keeps in step by hand (WP-1.5's shape).
            [.. payment.AllowedTransitions.Select(status => status.ToString())],
            payment.IsSettled,
            payment.ProviderName,
            payment.ProviderReference,
            payment.ProviderMessage,
            payment.RequestedAt,
            payment.SettledAt,
            payment.StatusReason,
            payment.ActorId,
            payment.ActorName);
    }
}

/// <summary>What taking a payment came to, as the API returns it.</summary>
/// <param name="Payment">The attempt, as it now stands.</param>
/// <param name="Approved">Whether the money moved. The one field a caller branches on.</param>
/// <param name="BillNumber">The bill it was taken against.</param>
/// <param name="BalanceBefore">What was owed on that bill when the money was asked for.</param>
public sealed record TakePaymentResponse(
    PaymentResponse Payment,
    bool Approved,
    string BillNumber,
    decimal BalanceBefore)
{
    /// <summary>Projects a payment result for the wire.</summary>
    public static TakePaymentResponse From(PaymentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new TakePaymentResponse(
            PaymentResponse.From(result.Payment),
            result.Payment.IsSettled,
            result.Bill.BillNumber,
            result.Payment.BalanceBefore);
    }
}

/// <summary>The payments register's HTTP surface.</summary>
public static class PaymentEndpoints
{
    /// <summary>Route prefix of the payments register.</summary>
    public const string RoutePrefix = "/api/payments";

    /// <summary>Default page size for a payment list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the payments endpoints.</summary>
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var payments = endpoints.MapGroup(RoutePrefix).WithTags("Payments");

        // The register-wide list, and with ?settledOnly=true the day's takings. Filtered on the
        // server; there is no sort and no total, the same shape every GridCore registry list has.
        payments
            .MapGet("/", async (
                    Guid? serviceAccountId,
                    Guid? customerId,
                    Guid? billId,
                    PaymentStatus? status,
                    bool? settledOnly,
                    int? limit,
                    [FromServices] IPaymentService register,
                    CancellationToken cancellationToken) =>
                Results.Ok((await register.ListAsync(
                        new PaymentQuery(serviceAccountId, customerId, billId, status, settledOnly, limit ?? DefaultPageSize),
                        cancellationToken))
                    .Select(PaymentResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Payments.Read)
            .WithName("ListPayments");

        payments
            .MapGet("/{id:guid}", async (
                    [FromRoute] Guid id,
                    [FromServices] IPaymentService register,
                    CancellationToken cancellationToken) =>
                await register.FindAsync(id, cancellationToken) is { } payment
                    ? Results.Ok(PaymentResponse.From(payment))
                    : PaymentProblems.PaymentNotFound(id))
            .RequirePermission(Permissions.Payments.Read)
            .WithName("GetPayment");

        // Taking money. A POST to the collection rather than a sub-resource of the bill: the thing
        // created is a payment, it is listed and read as one, and it exists whether or not the
        // provider approved it.
        //
        // A REFUSAL IS A 200. The provider declining is an answer, not a failed request — the
        // attempt is recorded, returned with status Declined, and the caller reads `approved`. A
        // 4xx here would be a 4xx with a committed row behind it, which is the one response nobody
        // can act on. The 4xx failure paths are the ones where nothing was recorded: no such bill
        // (404), a bill nobody owes (409), and more than is outstanding on it (409).
        payments
            .MapPost("/", (
                    TakePaymentRequest body,
                    [FromServices] IPaymentService register,
                    CancellationToken cancellationToken) =>
                PaymentProblems.RunAsync(async () =>
                    Results.Ok(TakePaymentResponse.From(await register.TakeAsync(
                        new TakePaymentInput(body.BillId, body.Amount, body.Method, body.Instrument),
                        cancellationToken)))))
            .RequirePermission(Permissions.Payments.Record)
            .WithValidation<TakePaymentRequest>()
            .WithName("TakePayment");

        // Note what is NOT here: a refund. payments.refund is declared, granted to Finance and to
        // Administrator, and opens no route at all — WP-2.5 treats Refunded as an outcome the seam
        // can carry and nothing more, because a refund needs a ledger to post the reversal into and
        // Finance's does not exist until WP-2.6. PaymentEndpointsTests asserts the permission is
        // still unclaimed, so the day a route does demand it, that is a deliberate act.
        return endpoints;
    }
}
