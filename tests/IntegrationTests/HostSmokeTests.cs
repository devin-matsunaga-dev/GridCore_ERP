using System.Net;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// The gate tier's floor: the real host boots against real infrastructure, its security pipeline
/// refuses an anonymous caller, and the shared fixture's Respawn reset actually empties the tables
/// a test wrote to. Everything here is tagged Category=Integration so the fast per-package loop
/// filters it out.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HostSmokeTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_host_boots_against_the_shared_containers_and_reports_itself_alive()
    {
        using var client = fixture.Application.CreateClient();

        using var response = await client.GetAsync(new Uri("/alive", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_by_the_fallback_policy()
    {
        using var client = fixture.Application.CreateClient();

        // Failure path, and the one that matters most: the host is secure by default (WP-0.3), so
        // an endpoint that forgot to opt in is still closed to a caller with no token.
        using var response = await client.GetAsync(new Uri(MeEndpoints.MeRoute, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Respawn_returns_the_database_to_an_empty_slate()
    {
        await using (var scope = fixture.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            platform.AuditEntries.Add(AuditEntry.For(
                DateTimeOffset.UtcNow,
                userId: "gate-suite",
                userName: "Gate suite",
                action: "gate.smoke",
                entityType: "SmokeTest",
                entityId: Guid.CreateVersion7().ToString(),
                before: null,
                after: new { Note = "written so the reset has something to remove" },
                correlationId: null));

            await platform.SaveChangesAsync();

            Assert.NotEqual(0, await platform.AuditEntries.CountAsync());
        }

        await fixture.ResetAsync();

        await using var verification = fixture.CreateScope();

        var context = verification.ServiceProvider.GetRequiredService<PlatformDbContext>();

        Assert.Equal(0, await context.AuditEntries.CountAsync());

        // The reset must not have taken the schema with it: EF still sees a migrated database.
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }
}
