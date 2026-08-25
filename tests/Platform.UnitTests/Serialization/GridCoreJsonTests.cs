using System.Text.Json;
using System.Text.Json.Serialization;
using GridCore.Platform.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GridCore.Platform.UnitTests.Serialization;

/// <summary>
/// The wire's one convention: an enum is a name, in both directions.
/// </summary>
/// <remarks>
/// Written after the demonstration screen — GridCore's first caller that POSTs from a browser —
/// was refused with <c>400 Failed to read CreateCustomerRequest</c> for sending
/// <c>"class": "Residential"</c>, which is the same word the API returns for that field. Nothing
/// had caught it because until then every write in every tier went through a module service or
/// built the request record in C#, so no test had ever crossed JSON in the reading direction.
/// </remarks>
public sealed class GridCoreJsonTests
{
    /// <summary>Stands in for the shape every enum-bearing request DTO has.</summary>
    private sealed record Body(string Name, Fruit Choice);

    private enum Fruit
    {
        Mango = 0,
        Breadfruit = 1,
    }

    [Fact]
    public void An_enum_is_read_from_its_name()
    {
        var body = JsonSerializer.Deserialize<Body>("""{"name":"Ana","choice":"Breadfruit"}""", GridCoreJson.Options());

        Assert.NotNull(body);
        Assert.Equal(Fruit.Breadfruit, body.Choice);
    }

    [Fact]
    public void An_enum_is_written_as_its_name()
    {
        // The half every response DTO does by hand. Configuring it here means a DTO that ever stops
        // hand-stringifying still emits the word rather than silently switching to an ordinal.
        var json = JsonSerializer.Serialize(new Body("Ana", Fruit.Breadfruit), GridCoreJson.Options());

        Assert.Contains("\"Breadfruit\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enum_is_still_read_from_its_number()
    {
        // Backwards compatibility, and it matters: an ordinal is what the host demanded before this
        // converter existed, so anything already posting one keeps working.
        var body = JsonSerializer.Deserialize<Body>("""{"name":"Ana","choice":1}""", GridCoreJson.Options());

        Assert.NotNull(body);
        Assert.Equal(Fruit.Breadfruit, body.Choice);
    }

    [Fact]
    public void A_name_that_is_not_a_member_is_refused_rather_than_defaulted()
    {
        // Failure path, and the one that must not go quiet: defaulting an unrecognised value to the
        // zero member would turn a typo into a customer silently registered as the wrong class.
        var thrown = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Body>("""{"name":"Ana","choice":"Papaya"}""", GridCoreJson.Options()));

        // The offending value is not quoted back — System.Text.Json names the path instead, which
        // is what the endpoint's 400 body ends up carrying.
        Assert.Equal("$.choice", thrown.Path);
    }

    [Fact]
    public void Configuring_twice_leaves_one_converter()
    {
        // A host registers these options and a test may build its own; two converters for the same
        // types is the kind of thing that works until the day the order changes.
        var options = GridCoreJson.Configure(GridCoreJson.Options());

        Assert.Single(options.Converters, converter => converter is JsonStringEnumConverter);
    }

    [Fact]
    public void The_minimal_api_pipeline_is_configured_by_AddGridCoreJson()
    {
        // The registration is what Program.cs calls, so it is the registration that is asserted —
        // options configured on an instance nothing resolves would pass every test above and still
        // leave the host returning 400.
        var services = new ServiceCollection().AddGridCoreJson().BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        var body = JsonSerializer.Deserialize<Body>("""{"name":"Ana","choice":"Breadfruit"}""", options);

        Assert.NotNull(body);
        Assert.Equal(Fruit.Breadfruit, body.Choice);
    }
}
