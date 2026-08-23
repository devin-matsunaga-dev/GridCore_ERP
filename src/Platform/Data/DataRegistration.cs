using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GridCore.Platform.Data;

/// <summary>
/// How a module registers its <see cref="DbContext"/> so that it shares the host's connection and
/// takes part in <see cref="IUnitOfWork"/>. A context registered with plain
/// <c>AddDbContext</c> gets its own connection and therefore its own transaction — which silently
/// breaks invariants 1 and 2 — so modules use these helpers instead.
/// </summary>
public static class DataRegistration
{
    /// <summary>
    /// Registers the per-scope shared connection and the unit of work over it.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="connectionFactory">
    /// Creates the connection for a scope. The host passes a new <c>NpgsqlConnection</c>; the fast
    /// test tier passes its SQLite in-memory connection wrapped as not-owned.
    /// </param>
    public static IServiceCollection AddGridCoreDataAccess(
        this IServiceCollection services,
        Func<IServiceProvider, GridCoreDbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        services.TryAddScoped(connectionFactory);
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }

    /// <summary>
    /// Registers a module's context on the shared connection and enlists it in the unit of work.
    /// </summary>
    /// <typeparam name="TContext">The module's context. It owns its own Postgres schema.</typeparam>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configure">
    /// Provider configuration for the context, given the shared connection —
    /// <c>(builder, connection) =&gt; builder.UseNpgsql(connection, npgsql =&gt; ...)</c>.
    /// </param>
    public static IServiceCollection AddGridCoreDbContext<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder, DbConnection> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddDbContext<TContext>((provider, builder) =>
            configure(builder, provider.GetRequiredService<GridCoreDbConnection>().Connection));

        // Deliberately additive, not TryAdd: every registered context is a participant, and the
        // unit of work resolves the whole set as IEnumerable.
        services.AddScoped(provider => new UnitOfWorkParticipant(provider.GetRequiredService<TContext>()));

        return services;
    }
}
