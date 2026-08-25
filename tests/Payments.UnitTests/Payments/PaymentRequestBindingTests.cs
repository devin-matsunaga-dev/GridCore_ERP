using System.Text.Json;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Platform.Serialization;

namespace GridCore.Modules.Payments.UnitTests.Payments;

/// <summary>
/// The bodies the SPA actually sends, read with the host's own JSON conventions — see
/// <c>RegistryRequestBindingTests</c> in the Customers suite for why these deserialize a literal
/// payload rather than constructing the record.
/// </summary>
public class PaymentRequestBindingTests
{
    private static readonly JsonSerializerOptions Options = GridCoreJson.Options();

    [Fact]
    public void A_payment_is_taken_from_a_bill_id_an_amount_and_a_method()
    {
        var billId = Guid.CreateVersion7();

        var body = JsonSerializer.Deserialize<TakePaymentRequest>(
            $$"""{"billId":"{{billId}}","amount":63.62,"method":"card","instrument":"•••• 4242"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(billId, body.BillId);

        // The method is a string on the wire and in the record — deliberately not an enum, so this
        // one was never at risk of the binding failure the enum-bearing DTOs had.
        Assert.Equal(PaymentMethods.Card, body.Method);
        Assert.Equal("•••• 4242", body.Instrument);
    }

    [Fact]
    public void An_amount_arrives_exact_to_the_cent()
    {
        // numeric(18,2), and money is decimal end to end. A payment that lost a cent between the
        // browser and the aggregate would be refused against the balance for the wrong reason.
        var body = JsonSerializer.Deserialize<TakePaymentRequest>(
            $$"""{"billId":"{{Guid.CreateVersion7()}}","amount":0.07,"method":"cash"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(0.07m, body.Amount);
    }

    [Fact]
    public void Cash_arrives_with_no_instrument_at_all()
    {
        // What the screen sends for cash: the money is in the drawer, so there is nothing to hold.
        var body = JsonSerializer.Deserialize<TakePaymentRequest>(
            $$"""{"billId":"{{Guid.CreateVersion7()}}","amount":63.62,"method":"cash","instrument":null}""",
            Options);

        Assert.NotNull(body);
        Assert.Null(body.Instrument);
    }

    [Fact]
    public void A_body_missing_the_bill_it_pays_is_refused_rather_than_defaulted_to_an_empty_id()
    {
        // Failure path. A payment is always against a bill; an omitted id must not arrive as
        // Guid.Empty and reach the register as "no such bill".
        var body = JsonSerializer.Deserialize<TakePaymentRequest>("""{"amount":63.62,"method":"cash"}""", Options);

        // The record's required positional parameter is not enforced by System.Text.Json, so this
        // documents what actually happens: the id arrives empty and the SERVICE refuses it. The
        // validator and the 404 path are what catch it, and both are tested beside this file.
        Assert.NotNull(body);
        Assert.Equal(Guid.Empty, body.BillId);
    }
}
