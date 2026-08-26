using System.Text.Json;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Platform.Serialization;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The bodies the SPA actually sends, read with the host's own JSON conventions.
/// </summary>
/// <remarks>
/// <para>
/// The registry's rules are tested against typed inputs everywhere else in this project, which is
/// the right place for them — but it means nothing here had ever crossed JSON in the <i>reading</i>
/// direction. The demonstration screen, GridCore's first caller that POSTs from a browser, was
/// refused with <c>400 Failed to read CreateCustomerRequest</c> for sending
/// <c>"class": "Residential"</c> — the same word this API returns for that field.
/// </para>
/// <para>
/// So these tests deserialize the literal payload rather than constructing the record: a DTO built
/// in C# proves the shape compiles and proves nothing at all about what the host can read.
/// </para>
/// </remarks>
public class RegistryRequestBindingTests
{
    private static readonly JsonSerializerOptions Options = GridCoreJson.Options();

    [Fact]
    public void A_customer_is_registered_from_the_class_name_the_API_returns()
    {
        var body = JsonSerializer.Deserialize<CreateCustomerRequest>(
            """{"name":"Reyes Family Residence","class":"Residential","contactName":"Ana Reyes"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal("Reyes Family Residence", body.Name);
        Assert.Equal(CustomerClass.Residential, body.Class);
        Assert.Equal("Ana Reyes", body.ContactName);

        // Omitted fields keep the record's own defaults rather than arriving as something else.
        Assert.Null(body.Email);
        Assert.Null(body.Phone);
    }

    [Fact]
    public void A_status_change_is_read_from_the_status_and_reason_code_names()
    {
        var body = JsonSerializer.Deserialize<ChangeCustomerStatusRequest>(
            """{"status":"Suspended","reasonCode":"UnpaidBalance","effectiveOn":"2026-09-01","notes":"Third reminder unanswered."}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(CustomerStatus.Suspended, body.Status);
        Assert.Equal(TransitionReasonCode.UnpaidBalance, body.ReasonCode);
        Assert.Equal(new DateOnly(2026, 9, 1), body.EffectiveOn);
        Assert.Equal("Third reminder unanswered.", body.Notes);
    }

    [Fact]
    public void A_transition_with_no_effective_date_arrives_as_null_rather_than_as_a_default_date()
    {
        // The difference matters: null means "the host dates it today", while DateOnly's own default
        // is 0001-01-01 — a date that would sail past every "not before" guard in the register.
        var body = JsonSerializer.Deserialize<ChangeCustomerClassRequest>(
            """{"class":"Commercial","reasonCode":"PremiseNowTrading"}""",
            Options);

        Assert.NotNull(body);
        Assert.Null(body.EffectiveOn);
        Assert.Null(body.Notes);
    }

    [Fact]
    public void A_premise_is_registered_from_a_nested_address()
    {
        var body = JsonSerializer.Deserialize<ServiceLocationRequest>(
            """
            {"address":{"line1":"77 As Nieves Road","city":"Songsong","region":"Rota","country":"MP"},
             "description":"Meter on the north wall"}
            """,
            Options);

        Assert.NotNull(body);
        Assert.Equal("77 As Nieves Road", body.Address.Line1);
        Assert.Equal("Rota", body.Address.Region);
        Assert.True(body.IsActive);
    }

    [Fact]
    public void An_account_is_opened_from_the_two_ids_that_pair_a_customer_with_a_premise()
    {
        var customerId = Guid.CreateVersion7();
        var locationId = Guid.CreateVersion7();

        var body = JsonSerializer.Deserialize<OpenServiceAccountRequest>(
            $$"""{"customerId":"{{customerId}}","serviceLocationId":"{{locationId}}","reason":"Requested at the counter"}""",
            Options);

        Assert.NotNull(body);
        Assert.Equal(customerId, body.CustomerId);
        Assert.Equal(locationId, body.ServiceLocationId);
    }

    [Fact]
    public void A_class_that_is_not_one_of_ours_is_refused_rather_than_defaulted()
    {
        // Failure path. Residential is the zero member, so a value quietly defaulted here would
        // register a commercial customer on a domestic tariff and nothing would ever say so.
        var thrown = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateCustomerRequest>(
            """{"name":"Reyes Family Residence","class":"Industrial"}""",
            Options));

        Assert.Equal("$.class", thrown.Path);
    }
}
