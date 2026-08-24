using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests.Infrastructure;

/// <summary>
/// The real <c>Web.Host</c> composition — every module, the bus, the outbox and the security
/// pipeline — booted in-process against the gate fixture's containers. The gate tier runs the
/// application as it actually ships rather than a hand-assembled subset of it, which is the only
/// way a composition mistake (a consumer registered after the bus, a missing connection string)
/// shows up in a test rather than at <c>aspire run</c>.
/// </summary>
/// <remarks>
/// Configuration reaches the host as <b>environment variables</b>, set by
/// <see cref="GateFixture"/> before this factory is constructed — not through
/// <c>ConfigureAppConfiguration</c>. Under the minimal hosting model <c>Program.cs</c> reads
/// <c>builder.Configuration</c> while it composes the app, which is before the test host's
/// configuration callbacks run; the connection strings would arrive after the host had already
/// thrown for the lack of them. Environment variables are also how the AppHost supplies these in
/// development and how a deploy supplies them in production, so the gate tier feeds the host
/// exactly the way the real world does.
/// </remarks>
public sealed class GridCoreApplication(Action<IServiceCollection> configureTestServices)
    : WebApplicationFactory<Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTestServices(configureTestServices);
    }
}
