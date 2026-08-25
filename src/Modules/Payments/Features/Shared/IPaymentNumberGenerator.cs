using GridCore.Modules.Payments.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Payments.Features.Shared;

/// <summary>
/// The prefix this module's registry numbers are issued under. The <i>shape</i> of a number is the
/// platform's (<see cref="RegistryNumbers"/>); what letters a payment number carries is the
/// Payments module's own business.
/// </summary>
public static class PaymentNumbers
{
    /// <summary>
    /// Prefix of a payment number, e.g. <c>PAY-000001</c>. Three letters, like <c>BIL-</c> and
    /// <c>MTR-</c> and for the same reason: it is read out over the phone, quoted on a receipt and
    /// written on a bank reconciliation beside the bill number it settles, and a one-letter prefix
    /// would be one character away from too many other things.
    /// </summary>
    public const string PaymentNumberPrefix = "PAY-";
}

/// <summary>
/// Issues the next payment number. A seam, so the numbering scheme is one registration away from
/// changing — a utility migrating from a legacy cashiering system usually has to keep its own.
/// </summary>
public interface IPaymentNumberGenerator
{
    /// <summary>The next unused payment number.</summary>
    Task<string> NextPaymentNumberAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues the payment series from the highest number already issued, inside the caller's
/// transaction.
/// </summary>
/// <remarks>
/// One <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column; the race with a
/// concurrent registration and the ordering trade it depends on are documented there, because every
/// registry shares them. There is no batch form here on purpose — payments are taken one at a time,
/// by a person, and a module that never writes a batch does not need
/// <see cref="RegistryNumberSeries.NextManyAsync"/>.
/// </remarks>
public sealed class SequentialPaymentNumberGenerator(PaymentsDbContext database) : IPaymentNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextPaymentNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            PaymentNumbers.PaymentNumberPrefix,
            database.Payments
                .Where(payment => payment.PaymentNumber.StartsWith(PaymentNumbers.PaymentNumberPrefix))
                .OrderByDescending(payment => payment.PaymentNumber)
                .Select(payment => payment.PaymentNumber),
            cancellationToken);
}
