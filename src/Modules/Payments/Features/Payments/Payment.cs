using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Payments.Features.Payments;

/// <summary>
/// How the money was tendered. A small closed set rather than free text: it is stamped on the
/// receipt, it decides what the payment provider is even asked, and it is the first thing a
/// cashing-up report groups by.
/// </summary>
public static class PaymentMethods
{
    /// <summary>Card, present or not. The ordinary case, and the one the provider actually decides.</summary>
    public const string Card = "card";

    /// <summary>A transfer from the customer's bank. Answered by the provider like a card.</summary>
    public const string BankTransfer = "bank-transfer";

    /// <summary>
    /// Notes and coins over the counter. Passed to the provider like the rest — the seam is what
    /// records the receipt — but the money is already in the drawer, which is why the sandbox never
    /// refuses one.
    /// </summary>
    public const string Cash = "cash";

    /// <summary>Every method this module accepts.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Card, BankTransfer, Cash };

    /// <summary>Whether <paramref name="method"/> is one of them.</summary>
    public static bool IsKnown(string? method) => method is not null && All.Contains(method);
}

/// <summary>
/// One attempt to take money from a customer: what was asked for, what the provider answered, and
/// what the utility therefore holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every attempt is a row, including the refusals.</b> A declined payment is not a failed request
/// to be discarded — it is the answer to "why does this customer still owe us money", it is what a
/// clerk reads back over the phone, and three declines in a morning are a fact somebody needs. The
/// register is append-only in the same spirit as the reading register: a retry is a new payment,
/// never this one revived.
/// </para>
/// <para>
/// <b>Everything a receipt needs is on the payment.</b> The account number, the customer's name, the
/// bill number, the currency, the provider and its reference. That is not denormalisation for speed
/// — every one of those facts belongs to another module that is free to change it, and a receipt
/// re-resolved at read time would give a customer a different document on a second look. The same
/// call <c>Bill</c> makes, for the same reason.
/// </para>
/// <para>
/// <b>What this class does NOT do is move a balance.</b> Reducing what a bill is owed is
/// <c>Bill.RecordPayment</c>, inside Billing, reached by consuming <c>PaymentApproved</c>. Payments
/// states the fact that money arrived; Billing decides what that does to the document, and Finance
/// what it does to the ledger. A payment that wrote to a bill would be a second module owning it.
/// </para>
/// </remarks>
public sealed class Payment
{
    /// <summary>Longest stored form of a status or outcome name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest name stored — a customer's, a provider's.</summary>
    public const int NameLength = 256;

    /// <summary>Longest reason recorded against a transition, or message returned by a provider.</summary>
    public const int ReasonLength = 512;

    /// <summary>Longest provider reference stored. Gateways issue long opaque strings.</summary>
    public const int ProviderReferenceLength = 128;

    /// <summary>Longest method name stored.</summary>
    public const int MethodLength = 32;

    /// <summary>
    /// Longest instrument label stored. A masked tail or a mandate reference — never a card number,
    /// which GridCore does not take and is not in scope to hold.
    /// </summary>
    public const int InstrumentLength = 64;

    /// <summary>Total digits a money column stores.</summary>
    public const int MoneyPrecision = Money.Precision;

    /// <summary>Decimal places a money column stores.</summary>
    public const int MoneyScale = Money.DecimalPlaces;

    private Payment()
    {
        // EF materialisation.
        PaymentNumber = string.Empty;
        AccountNumber = string.Empty;
        CustomerName = string.Empty;
        BillNumber = string.Empty;
        Currency = string.Empty;
        Method = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this payment. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number on the receipt, e.g. <c>PAY-000001</c>. Unique across payments.</summary>
    public string PaymentNumber { get; private init; }

    /// <summary>The service account credited, in the Customers schema.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Its number, as printed on the receipt.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>The customer who paid.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Their name at the time they paid.</summary>
    public string CustomerName { get; private init; }

    /// <summary>The bill settled, in the Billing schema.</summary>
    public Guid BillId { get; private init; }

    /// <summary>Its number, as printed — what a bank reconciliation is matched on.</summary>
    public string BillNumber { get; private init; }

    /// <summary>How much was asked for. What was <i>taken</i> is this, or nothing at all.</summary>
    public decimal Amount { get; private init; }

    /// <summary>ISO 4217 code the amount is expressed in. Always the bill's.</summary>
    public string Currency { get; private init; }

    /// <summary>How it was paid. One of <see cref="PaymentMethods"/>.</summary>
    public string Method { get; private init; }

    /// <summary>
    /// The instrument charged, as the utility is allowed to hold it — a masked card tail, a mandate
    /// reference, or <see langword="null"/> for cash.
    /// </summary>
    public string? Instrument { get; private init; }

    /// <summary>What the balance on the bill was when the payment was taken, for reconciliation.</summary>
    public decimal BalanceBefore { get; private init; }

    /// <summary>Where the attempt stands.</summary>
    public PaymentStatus Status { get; private set; }

    /// <summary>
    /// The provider's verbatim answer, or <see langword="null"/> while the payment is pending.
    /// Stored beside <see cref="Status"/> rather than folded into it: the status says what the
    /// utility holds, the outcome says what the provider said, and a clerk on the phone needs the
    /// second to explain the first.
    /// </summary>
    public PaymentOutcome? Outcome { get; private set; }

    /// <summary>
    /// What answered — the simulated sandbox here, a real gateway in production. Stamped because a
    /// record of where money came from outlives whichever implementation was configured at the time.
    /// </summary>
    public string? ProviderName { get; private set; }

    /// <summary>The provider's own reference, for reconciliation. Present on refusals too.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>What the provider said about it, where anything was said.</summary>
    public string? ProviderMessage { get; private set; }

    /// <summary>When the payment was taken.</summary>
    public DateTimeOffset RequestedAt { get; private init; }

    /// <summary>When the provider answered.</summary>
    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>Why it last moved.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Subject id of whoever took the payment.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>Whether the utility actually holds this money.</summary>
    public bool IsSettled => PaymentTransitions.IsSettled(Status);

    /// <summary>The statuses this payment may move to, for rendering transition buttons.</summary>
    public IReadOnlyList<PaymentStatus> AllowedTransitions => PaymentTransitions.AllowedFrom(Status);

    /// <summary>
    /// Takes a payment against a bill and holds it as <see cref="PaymentStatus.Pending"/>, ready to
    /// be put to the provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bill's balance is checked here, before anybody is charged.</b> A payment larger than
    /// what is owed is refused rather than authorised: the utility must not take money the bill
    /// cannot accept, because <c>Bill.RecordPayment</c> refuses an overpayment outright and the
    /// alternative — quietly absorbing the difference — would leave a credit with no record of
    /// where it went. A credit balance is Finance's to hold, and until it can, this is the honest
    /// answer. Word for word the call <c>Bill.Adjust</c> makes about an over-large credit.
    /// </para>
    /// <para>
    /// It is a pre-check, not a lock: the balance could move between this and the consumer that
    /// applies it. That race ends safely — <c>Bill.RecordPayment</c> refuses the overpayment and
    /// the consumer's message is retried and then faulted, rather than a bill silently going
    /// negative. Narrowing the window further needs a reservation, which is a real feature with a
    /// real expiry policy behind it.
    /// </para>
    /// </remarks>
    /// <param name="paymentNumber">The number to print on the receipt, already reserved by the caller.</param>
    /// <param name="account">Who is paying, from the Customers module's directory.</param>
    /// <param name="bill">What is being paid, from the Billing module's directory.</param>
    /// <param name="amount">How much, always positive and exact to the cent.</param>
    /// <param name="method">How it is being paid. One of <see cref="PaymentMethods"/>.</param>
    /// <param name="instrument">The instrument charged, where the method has one.</param>
    /// <param name="actor">Who took it.</param>
    /// <param name="now">The clock, for the row's own identity and timestamp.</param>
    /// <exception cref="PaymentValidationException">
    /// The number is missing, the amount is not positive or is finer than a cent, the method is not
    /// one this module accepts, or the bill and the account do not belong together.
    /// </exception>
    /// <exception cref="PaymentWorkflowException">
    /// The bill is not owed, or the payment is larger than its balance.
    /// </exception>
    public static Payment Take(
        string paymentNumber,
        ServiceAccountSummary account,
        BillSummary bill,
        decimal amount,
        string method,
        string? instrument,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before anything is built — WP-1.4's ordering rule.
        if (string.IsNullOrWhiteSpace(paymentNumber))
        {
            throw new PaymentValidationException("A payment must be given a number before it can be taken.");
        }

        if (amount <= Money.Zero)
        {
            throw new PaymentValidationException($"A payment must be positive; '{amount}' is not.");
        }

        if (!Money.IsRounded(amount))
        {
            // Refused rather than rounded: this is a figure somebody at a counter typed, not one
            // GridCore computed. The rule Money is explicit about, and the same call
            // Bill.RecordPayment makes about the very same number one module downstream.
            throw new PaymentValidationException($"A payment is taken to the cent; '{amount}' is finer than that.");
        }

        if (!PaymentMethods.IsKnown(method))
        {
            // Refused rather than stored as typed. The method decides what the provider is asked and
            // how the day is cashed up, so an unknown one is a payment nobody can reconcile.
            throw new PaymentValidationException(
                $"'{method}' is not a payment method this utility accepts; expected one of {string.Join(", ", PaymentMethods.All)}.");
        }

        if (bill.ServiceAccountId != account.Id)
        {
            // The caller named a bill and an account that have nothing to do with each other. A
            // validation failure rather than a workflow conflict: no state would make it legal.
            throw new PaymentValidationException(
                $"Bill {bill.BillNumber} belongs to another service account and cannot be paid on {account.AccountNumber}.");
        }

        if (!bill.IsOutstanding)
        {
            throw new PaymentWorkflowException(
                $"Bill {bill.BillNumber} is {bill.Status} and is not owed, so there is nothing to pay against it. "
                + (string.Equals(bill.Status, "Draft", StringComparison.Ordinal)
                    ? "A draft has not been sent, so nobody has been asked for the money."
                    : "Money moving after a bill is settled is a refund, not a payment."));
        }

        if (amount > bill.Balance)
        {
            // THE MONEY GUARD this work package is about. Refused before the provider is asked, so
            // the utility never authorises money the bill cannot accept.
            throw new PaymentWorkflowException(
                $"Bill {bill.BillNumber} has {bill.Balance} outstanding; a payment of {amount} is more than is owed.");
        }

        return new Payment
        {
            Id = Guid.CreateVersion7(now),
            PaymentNumber = paymentNumber.Trim(),
            ServiceAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            CustomerId = account.CustomerId,
            CustomerName = RegistryText.Clean(account.CustomerName, NameLength) ?? account.AccountNumber,
            BillId = bill.Id,
            BillNumber = bill.BillNumber,
            Amount = amount,
            Currency = bill.Currency,
            Method = method,

            // Cash has no instrument, and a label somebody typed for one is noise on a receipt.
            Instrument = string.Equals(method, PaymentMethods.Cash, StringComparison.Ordinal)
                ? null
                : RegistryText.Clean(instrument, InstrumentLength),
            BalanceBefore = bill.Balance,
            Status = PaymentStatus.Pending,
            RequestedAt = now,
            StatusChangedAt = now,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new PaymentValidationException("A payment must name who took it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };
    }

    /// <summary>What the provider is asked, built from the payment rather than from the request.</summary>
    /// <remarks>
    /// The <see cref="Id"/> goes across as the idempotency key: a real gateway asked twice for the
    /// same one charges once. That is why the row is minted before the provider is called and not
    /// after — a payment created from an answer would have no key to have sent with the question.
    /// </remarks>
    public PaymentAuthorizationRequest ToAuthorization() =>
        new(Id, PaymentNumber, Amount, Currency, Method, Instrument);

    /// <summary>
    /// Records what the provider answered, moving the payment out of
    /// <see cref="PaymentStatus.Pending"/>.
    /// </summary>
    /// <remarks>
    /// <b>The mapping is here and nowhere else.</b> A provider reports an outcome; what that means
    /// for the utility is this module's business — which is the same split
    /// <c>IMeterReadingProvider</c> draws when it refuses to classify its own readings.
    /// </remarks>
    /// <param name="result">What came back.</param>
    /// <param name="providerName">What answered.</param>
    /// <param name="now">The clock, for the transition's timestamp.</param>
    /// <exception cref="PaymentWorkflowException">
    /// The payment has already been answered, or the provider answered with an outcome that is not
    /// a legal move from where it stands.
    /// </exception>
    /// <exception cref="PaymentValidationException">The provider returned no reference to reconcile against.</exception>
    public void Settle(PaymentAuthorizationResult result, string providerName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (Status is not PaymentStatus.Pending)
        {
            // A provider that answered twice, or a retry that reached a payment already settled.
            // Refused rather than overwritten: the first answer is the one the money followed, and
            // an approved payment quietly re-stamped as declined is money with no record.
            throw new PaymentWorkflowException(
                $"Payment {PaymentNumber} is already {Status} and has been answered; a second attempt is a new payment.");
        }

        if (RegistryText.Clean(result.ProviderReference, ProviderReferenceLength) is not { } reference)
        {
            // Every outcome carries one, refusals included. A payment with no provider reference is
            // a payment nobody can reconcile against a statement.
            throw new PaymentValidationException(
                $"The provider answered payment {PaymentNumber} with no reference to reconcile against.");
        }

        Move(StatusFor(result.Outcome), now, result.Message);

        Outcome = result.Outcome;
        ProviderName = RegistryText.Clean(providerName, NameLength);
        ProviderReference = reference;
        ProviderMessage = RegistryText.Clean(result.Message, ReasonLength);
        SettledAt = result.ProcessedAt;
    }

    /// <summary>
    /// What an outcome means for the utility.
    /// </summary>
    /// <exception cref="PaymentValidationException">The outcome is not one this module knows.</exception>
    internal static PaymentStatus StatusFor(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Approved => PaymentStatus.Approved,

        // Both refusals, told apart on the payment's outcome rather than in its status: no money
        // moved either way, and what a screen renders differently is the reason, not the state.
        PaymentOutcome.Declined => PaymentStatus.Declined,
        PaymentOutcome.InsufficientFunds => PaymentStatus.Declined,

        // NOT a decline. The money may have moved and the answer been lost, so the payment failed
        // and is reconciled against the provider rather than assumed away.
        PaymentOutcome.Timeout => PaymentStatus.Failed,

        PaymentOutcome.Refunded => PaymentStatus.Refunded,

        // An enum value cast in from a provider GridCore does not fully know. Refused rather than
        // defaulted to either approved or declined: guessing whether money moved is the one thing
        // worse than failing. The same call BillAdjustment.Signed makes about an unknown kind.
        _ => throw new PaymentValidationException($"'{outcome}' is not an outcome this utility knows how to record."),
    };

    /// <summary>
    /// The one place the status moves, so no path can move it without checking the machine first.
    /// </summary>
    /// <exception cref="PaymentWorkflowException">
    /// The move is not one <see cref="PaymentTransitions"/> allows.
    /// </exception>
    private void Move(PaymentStatus to, DateTimeOffset now, string? reason)
    {
        if (!PaymentTransitions.IsAllowed(Status, to))
        {
            throw new PaymentWorkflowException(
                $"Payment {PaymentNumber} is {Status} and cannot move to {to}. "
                + $"Allowed from {Status}: {(AllowedTransitions.Count is 0 ? "nothing — it is final" : string.Join(", ", AllowedTransitions))}.");
        }

        Status = to;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);
    }
}
