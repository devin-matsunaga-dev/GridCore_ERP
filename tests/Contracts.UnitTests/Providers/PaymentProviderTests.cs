using GridCore.Contracts.Providers;

namespace GridCore.Contracts.UnitTests.Providers;

/// <summary>
/// The payment provider seam itself — the shape every implementation, sandbox or real gateway, has
/// to fit. Nothing here knows what a bill is, which is the point.
/// </summary>
public sealed class PaymentProviderTests
{
    [Fact]
    public void The_seam_carries_every_outcome_the_work_package_names() =>
        // Approved, Declined, InsufficientFunds, Timeout, Refunded. Widening this set later would
        // mean revisiting every consumer that switches over it, so it is declared whole up front —
        // even though nothing performs a refund yet.
        Assert.Equal(
            ["Approved", "Declined", "InsufficientFunds", "Timeout", "Refunded"],
            Enum.GetNames<PaymentOutcome>());

    [Fact]
    public void No_outcome_is_the_default_zero()
    {
        // A payment whose outcome column was never written must not read back as "approved" — or as
        // anything else. Every member is explicitly numbered from 1, so default(PaymentOutcome) is
        // not a real answer.
        Assert.All(Enum.GetValues<PaymentOutcome>(), outcome => Assert.NotEqual(0, (int)outcome));

        Assert.False(Enum.IsDefined((PaymentOutcome)0));
    }

    [Fact]
    public void An_authorization_request_carries_an_idempotency_key_and_a_stable_reference()
    {
        // The id is what a real gateway deduplicates on, so a retried request charges once. The
        // reference is the number the utility knows the attempt by — stable across machines, unlike
        // the id, which is what a simulator keys its determinism on.
        var request = new PaymentAuthorizationRequest(
            Guid.CreateVersion7(),
            "PAY-000001",
            49.95m,
            "USD",
            "card",
            "•••• 4242");

        Assert.NotEqual(Guid.Empty, request.PaymentId);
        Assert.Equal("PAY-000001", request.Reference);
        Assert.Equal(49.95m, request.Amount);
    }

    [Fact]
    public void An_amount_put_to_a_provider_is_an_exact_decimal()
    {
        var request = new PaymentAuthorizationRequest(
            Guid.CreateVersion7(),
            "PAY-000001",
            0.1m + 0.2m,
            "USD",
            "card",
            null);

        // 0.30 exactly. This is why money is decimal and never double, at the boundary as much as
        // inside it.
        Assert.Equal(0.3m, request.Amount);
    }

    [Fact]
    public void An_instrument_is_optional_because_cash_has_none() =>
        Assert.Null(new PaymentAuthorizationRequest(
            Guid.CreateVersion7(),
            "PAY-000001",
            10.00m,
            "USD",
            "cash",
            null).Instrument);
}
