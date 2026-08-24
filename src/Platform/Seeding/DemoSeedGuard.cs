using Microsoft.Extensions.Hosting;

namespace GridCore.Platform.Seeding;

/// <summary>
/// ARCHITECTURE.md invariant 8 in code: demo data is Development-only, and configuration may turn
/// it off but never on.
/// </summary>
/// <remarks>
/// The asymmetry is the point. A demo seeder invents customers, meters and bills; the damage of one
/// running against a real database is not undone by deleting rows, because by then the data has been
/// billed on and reported from. So the environment decides, and the configuration switch only
/// narrows what the environment already permits — there is deliberately no setting that seeds a
/// production database.
/// </remarks>
public static class DemoSeedGuard
{
    /// <summary>Whether demo seeding may run at all in <paramref name="environment"/>.</summary>
    /// <param name="environment">The host environment.</param>
    /// <param name="configured">
    /// The <c>Platform:SeedDemoData</c> setting. <see langword="null"/> means "not configured",
    /// which in Development means yes.
    /// </param>
    public static bool IsAllowed(IHostEnvironment environment, bool? configured)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() && (configured ?? true);
    }

    /// <summary>
    /// Throws unless demo seeding is permitted in <paramref name="environment"/>.
    /// </summary>
    /// <remarks>
    /// Defence in depth: <c>AddGridCorePlatform</c> already declines to register the runner outside
    /// Development, so reaching this check means something registered it deliberately — and that is
    /// exactly the case worth failing loudly.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The environment is not Development.</exception>
    public static void EnsureAllowed(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Demo data may only be seeded in Development; this host is '{environment.EnvironmentName}'. "
                + "Reference data the application needs ships by migration and is unaffected.");
        }
    }
}
