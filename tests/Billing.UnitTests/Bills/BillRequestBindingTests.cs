using System.Text.Json;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Platform.Serialization;

namespace GridCore.Modules.Billing.UnitTests.Bills;

/// <summary>
/// The bodies the SPA actually sends, read with the host's own JSON conventions — the billing half
/// of what <c>RegistryRequestBindingTests</c> covers for Customers. See that file for why these
/// deserialize a literal payload rather than constructing the record.
/// </summary>
public class BillRequestBindingTests
{
    private static readonly JsonSerializerOptions Options = GridCoreJson.Options();

    [Fact]
    public void A_run_is_read_from_its_cycle_code()
    {
        var body = JsonSerializer.Deserialize<RunBillingRequest>("""{"cycleCode":"DEMO-20260825-0930"}""", Options);

        Assert.NotNull(body);
        Assert.Equal("DEMO-20260825-0930", body.CycleCode);
    }

    [Fact]
    public void An_empty_body_issues_a_bill_on_the_aggregate_s_own_defaults()
    {
        // What the demonstration screen posts: issuing takes no decision from the caller, so the
        // body is `{}` and every field has to arrive absent rather than as a zero date.
        var body = JsonSerializer.Deserialize<IssueBillRequest>("{}", Options);

        Assert.NotNull(body);
        Assert.Null(body.IssuedOn);
        Assert.Null(body.DueDate);
        Assert.Null(body.Reason);
    }

    [Fact]
    public void An_adjustment_is_read_from_its_kind_name()
    {
        var body = JsonSerializer.Deserialize<AdjustBillRequest>(
            """{"kind":"Credit","amount":12.5,"reason":"Estimated read corrected after a site visit."}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(BillAdjustmentKind.Credit, body.Kind);

        // Money is decimal all the way in — 12.5 must not arrive via double.
        Assert.Equal(12.5m, body.Amount);
    }

    [Fact]
    public void An_adjustment_kind_that_is_not_one_of_ours_is_refused_rather_than_defaulted()
    {
        // Failure path, and the sharpest one in this file: Credit is the zero member, so a value
        // quietly defaulted here would turn a charge into a credit and take money off a bill.
        var thrown = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AdjustBillRequest>(
            """{"kind":"Writeoff","amount":12.5,"reason":"…"}""",
            Options));

        Assert.Equal("$.kind", thrown.Path);
    }
}
