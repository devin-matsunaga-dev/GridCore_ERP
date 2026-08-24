using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GridCore.Platform.Data;

/// <summary>
/// Conventions every GridCore <see cref="Microsoft.EntityFrameworkCore.DbContext"/> shares. There
/// are now several — the platform's plus one per module that persists anything — and each of them
/// is configured in three places (the module's registration, its design-time factory and the gate
/// fixture), so the triple lives here rather than being retyped and eventually mistyped.
/// </summary>
public static class GridCoreDbContexts
{
    /// <summary>
    /// Table that records applied migrations. One per schema, so a module's migration history sits
    /// beside the tables it describes and dropping a schema takes its history with it.
    /// </summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Npgsql configuration for a context that owns <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">The module's Postgres schema — also its <see cref="Modules.IModule.Name"/>.</param>
    public static Action<NpgsqlDbContextOptionsBuilder> InSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        return npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema);
    }
}
