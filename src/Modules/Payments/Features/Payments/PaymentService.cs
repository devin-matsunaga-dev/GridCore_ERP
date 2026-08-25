using GridCore.Contracts.Directories;
using GridCore.Contracts.Events;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Data;
using GridCore.Modules.Payments.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>What a caller supplies to take a payment.</summary>
/// <param name="BillId">The bill being settled.</param>
/// <param name="Amount">How much, always positive and exact to the cent.</param>
/// <param name="Method">How it is being paid. One of <see cref="PaymentMethods"/>.</param>
/// <param name="Instrument">
/// The instrument charged, as the utility is allowed to hold it — a masked card tail or a mandate
/// reference. Ignored for cash. <b>Never a full card number.</b>
/// </param>
public sealed record TakePaymentInput(Guid BillId, decimal Amount, string Method, string? Instrument = null);

/// <summary>What a caller filters the payments register by.</summary>
/// <param name="ServiceAccountId">Only payments on this account.</param>
/// <param name="CustomerId">Only payments by this customer.</param>
/// <param name="BillId">Only payments against this bill.</param>
/// <param name="Status">Only payments in this state.</param>
/// <param name="SettledOnly">Only money the utility actually holds.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record PaymentQuery(
    Guid? ServiceAccountId = null,
    Guid? CustomerId = null,
    Guid? BillId = null,
    PaymentStatus? Status = null,
    bool? SettledOnly = null,
    int Limit = 50);

/// <summary>What a payment came to, once the provider had answered.</summary>
/// <param name="Payment">The attempt, as it now stands.</param>
/// <param name="Bill">The bill it was taken against, as it stood when the money was asked for.</param>
/// <remarks>
/// The bill is the one it was <i>checked against</i>, not the one that results. Reducing a balance
/// happens in Billing, on the other side of <c>PaymentApproved</c> and therefore after this
/// transaction commits — a result that claimed to show the new balance would be showing a figure
/// nothing had written yet.
/// </remarks>
public sealed record PaymentResult(Payment Payment, BillSummary Bill);

/// <summary>The payments register. Endpoints are a thin layer over it.</summary>
public interface IPaymentService
{
    /// <summary>
    /// Takes a payment against a bill: checks what is owed, puts it to the provider, records what
    /// came back and — only if the money moved — publishes <see cref="PaymentApproved"/>.
    /// </summary>
    /// <exception cref="BillNotFoundException">There is no bill with that id.</exception>
    /// <exception cref="ServiceAccountNotFoundException">The bill's account is not one Customers knows.</exception>
    /// <exception cref="PaymentWorkflowException">
    /// The bill is not owed, or the payment is more than is outstanding on it.
    /// </exception>
    /// <exception cref="PaymentValidationException">The payment is not one this module can take.</exception>
    Task<PaymentResult> TakeAsync(TakePaymentInput input, CancellationToken cancellationToken = default);

    /// <summary>The payments register, newest first.</summary>
    Task<IReadOnlyList<Payment>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default);

    /// <summary>One payment, or <see langword="null"/> when there is no such id.</summary>
    Task<Payment?> FindAsync(Guid paymentId, CancellationToken cancellationToken = default);
}

/// <summary>The payments register over the payments schema.</summary>
/// <remarks>
/// <para>
/// Every write runs inside <see cref="IUnitOfWork.ExecuteAsync"/> and never calls
/// <c>SaveChanges</c> itself, so a payment, its audit entry and its <see cref="PaymentApproved"/>
/// outbox row are one transaction — invariants 1 and 2. That is what makes the approval and the
/// event inseparable: a payment cannot be recorded without Finance and Billing eventually hearing
/// about it, and the event cannot be published for a payment that was rolled back.
/// </para>
/// <para>
/// <b>The provider is called inside that transaction, deliberately.</b> It is the one outbound call
/// in the write, and holding the transaction open across it is the lesser evil: the alternative is
/// charging a customer and then failing to record it, which is money with no row. The sandbox
/// answers in microseconds; a real gateway would want the payment committed as
/// <see cref="PaymentStatus.Pending"/> first and settled by callback, which is a real integration
/// with a real reconciliation job behind it.
/// </para>
/// <para>
/// It reads nothing outside its own schema. Bills arrive through <see cref="IBillDirectory"/> and
/// accounts through <see cref="IServiceAccountDirectory"/>, both interfaces in <c>Contracts</c> —
/// this module has never heard of a <c>billing</c> or a <c>customers</c> schema.
/// </para>
/// </remarks>
public sealed class PaymentService(
    PaymentsDbContext database,
    IBillDirectory bills,
    IServiceAccountDirectory accounts,
    IPaymentProvider provider,
    IPaymentNumberGenerator numbers,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    IEventPublisher events,
    ICurrentUser currentUser,
    TimeProvider clock) : IPaymentService
{
    /// <summary>The largest page a list will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public Task<PaymentResult> TakeAsync(TakePaymentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();

                var bill = await bills.FindAsync(input.BillId, ct).ConfigureAwait(false)
                    ?? throw new BillNotFoundException(input.BillId);

                var account = await accounts.FindAsync(bill.ServiceAccountId, ct).ConfigureAwait(false)
                    ?? throw new ServiceAccountNotFoundException(bill.ServiceAccountId);

                // The row is minted before the provider is asked, because its id is the idempotency
                // key that goes across — and because every guard, including the balance check this
                // work package is about, lives on the aggregate and must run before anybody is
                // charged.
                var payment = Payment.Take(
                    await numbers.NextPaymentNumberAsync(ct).ConfigureAwait(false),
                    account,
                    bill,
                    input.Amount,
                    input.Method,
                    input.Instrument,
                    RegistryActor.Of(currentUser),
                    now);

                var answer = await provider
                    .AuthorizeAsync(payment.ToAuthorization(), ct)
                    .ConfigureAwait(false);

                payment.Settle(answer, provider.Name, now);

                database.Payments.Add(payment);

                // INVARIANT 1. Every attempt is audited, refusals included — a run of declines on
                // one account is exactly what somebody comes looking for, and an audit trail that
                // only recorded the successes would be one that could not answer them. There is no
                // before: a payment is a new fact, never an edit to an old one.
                audit.Record(
                    AuditActions.PaymentTaken,
                    AuditEntityTypes.Payment,
                    payment.Id.ToString(),
                    before: null,
                    after: PaymentSnapshot.Of(payment));

                // ONLY an approval is published. A decline is Payments' own business: no money
                // moved, so there is no receivable to relieve and no cash to post, and an event for
                // it would be one every consumer had to learn to ignore. PaymentApproved's own
                // documentation says so.
                if (payment.IsSettled)
                {
                    await events.PublishAsync(
                            PaymentApproved.For(
                                now,
                                payment.Id,
                                payment.ServiceAccountId,
                                payment.CustomerId,
                                payment.BillId,
                                payment.Amount,
                                payment.Currency,
                                payment.Method,
                                payment.ProviderReference!),
                            ct)
                        .ConfigureAwait(false);
                }

                return new PaymentResult(payment, bill);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Payment>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var payments = database.Payments.AsNoTracking();

        if (query.ServiceAccountId is { } serviceAccountId)
        {
            payments = payments.Where(payment => payment.ServiceAccountId == serviceAccountId);
        }

        if (query.CustomerId is { } customerId)
        {
            payments = payments.Where(payment => payment.CustomerId == customerId);
        }

        if (query.BillId is { } billId)
        {
            payments = payments.Where(payment => payment.BillId == billId);
        }

        if (query.Status is { } status)
        {
            payments = payments.Where(payment => payment.Status == status);
        }

        if (query.SettledOnly is true)
        {
            // Spelled out rather than calling PaymentTransitions.IsSettled: EF has to translate this
            // into SQL, and a method call over an enum is not something it can.
            payments = payments.Where(payment => payment.Status == PaymentStatus.Approved);
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike.
        return await payments
            .OrderByDescending(payment => payment.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Payment?> FindAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        database.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.Id == paymentId, cancellationToken);
}

/// <summary>
/// A payment as the audit trail stores it — flat, and holding the figures somebody reading the
/// trail would otherwise have to go and fetch.
/// </summary>
/// <param name="Id">Identifier of the payment.</param>
/// <param name="PaymentNumber">The number on the receipt.</param>
/// <param name="ServiceAccountId">The account credited.</param>
/// <param name="AccountNumber">Its number.</param>
/// <param name="BillId">The bill settled.</param>
/// <param name="BillNumber">Its number.</param>
/// <param name="Amount">How much was asked for.</param>
/// <param name="Currency">What it is expressed in.</param>
/// <param name="Method">How it was paid.</param>
/// <param name="BalanceBefore">What was owed on the bill when the payment was taken.</param>
/// <param name="Status">Where the attempt stands.</param>
/// <param name="Outcome">What the provider answered.</param>
/// <param name="ProviderName">What answered.</param>
/// <param name="ProviderReference">Its reference, for reconciliation.</param>
public sealed record PaymentSnapshot(
    Guid Id,
    string PaymentNumber,
    Guid ServiceAccountId,
    string AccountNumber,
    Guid BillId,
    string BillNumber,
    decimal Amount,
    string Currency,
    string Method,
    decimal BalanceBefore,
    PaymentStatus Status,
    PaymentOutcome? Outcome,
    string? ProviderName,
    string? ProviderReference)
{
    /// <summary>Takes a snapshot of <paramref name="payment"/> as it stands.</summary>
    /// <remarks>
    /// The instrument is deliberately absent. It is on the payment because a clerk needs to say
    /// which card was used; it is not on the audit trail, which is read far more widely and by
    /// people with no business knowing.
    /// </remarks>
    public static PaymentSnapshot Of(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new PaymentSnapshot(
            payment.Id,
            payment.PaymentNumber,
            payment.ServiceAccountId,
            payment.AccountNumber,
            payment.BillId,
            payment.BillNumber,
            payment.Amount,
            payment.Currency,
            payment.Method,
            payment.BalanceBefore,
            payment.Status,
            payment.Outcome,
            payment.ProviderName,
            payment.ProviderReference);
    }
}

