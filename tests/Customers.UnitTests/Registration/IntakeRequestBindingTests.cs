using System.Text.Json;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Platform.Serialization;

namespace GridCore.Modules.Customers.UnitTests.Registration;

/// <summary>
/// The intake body the wizard actually sends, read with the host's own JSON conventions.
/// </summary>
/// <remarks>
/// The debt WP-2.7 recorded: a module adding an enum-bearing request DTO owes one of these, because
/// nothing else in any tier crosses JSON in the reading direction. <see cref="CustomerClass"/> is on
/// this body and it is what the deposit is assessed from, so a name the host could not read would
/// be a 400 on the last step of a five-step form, and a name quietly defaulted would assess the
/// wrong deposit against the wrong class.
/// </remarks>
public class IntakeRequestBindingTests
{
    private static readonly JsonSerializerOptions Options = GridCoreJson.Options();

    [Fact]
    public void An_intake_is_read_from_the_class_name_the_API_returns()
    {
        var body = JsonSerializer.Deserialize<RegisterCustomerIntakeRequest>(
            """
            {
              "name": "Reyes Family Residence",
              "class": "Residential",
              "contactName": "Ana Reyes",
              "email": "ana.reyes@example.com",
              "phone": "+1-670-532-0199",
              "premise": {
                "newPremise": {
                  "address": {
                    "line1": "77 As Nieves Road",
                    "city": "Songsong",
                    "region": "Rota",
                    "country": "MP"
                  },
                  "description": "Meter on the north wall"
                }
              },
              "depositCollected": 75.00,
              "startService": true,
              "reason": "New connection"
            }
            """,
            Options);

        Assert.NotNull(body);
        Assert.Equal(CustomerClass.Residential, body.Class);
        Assert.Equal(75.00m, body.DepositCollected);
        Assert.True(body.StartService);
        Assert.Equal("Songsong", body.Premise.NewPremise!.Address.City);
        Assert.Null(body.Premise.ServiceLocationId);
    }

    [Fact]
    public void An_intake_at_an_existing_premise_is_read_from_its_id()
    {
        var id = Guid.CreateVersion7();

        var body = JsonSerializer.Deserialize<RegisterCustomerIntakeRequest>(
            $$"""
            {
              "name": "Songsong Village Market",
              "class": "Commercial",
              "premise": { "serviceLocationId": "{{id}}" }
            }
            """,
            Options);

        Assert.NotNull(body);
        Assert.Equal(CustomerClass.Commercial, body.Class);
        Assert.Equal(id, body.Premise.ServiceLocationId);
        Assert.Null(body.Premise.NewPremise);

        // The wizard's own defaults, not something else: an omitted deposit is none collected, and
        // an omitted start-service is an account opened and left unenergised.
        Assert.Equal(0m, body.DepositCollected);
        Assert.False(body.StartService);
    }

    [Fact]
    public void An_unrecognised_class_is_refused_rather_than_defaulted() =>
        // Pinned against the zero member. `CustomerClass.Residential` is what a silently defaulted
        // value would become, and a commercial premises quietly assessed a residential deposit is a
        // figure nobody would think to check.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<RegisterCustomerIntakeRequest>(
                """{"name":"Somebody","class":"Industrial","premise":{"serviceLocationId":"0199c0de-0000-7000-8000-000000000001"}}""",
                Options));
}
