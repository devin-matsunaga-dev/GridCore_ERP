using System.Data.Common;

namespace GridCore.Platform.Data;

/// <summary>
/// The one database connection every <see cref="Microsoft.EntityFrameworkCore.DbContext"/> in a
/// scope shares. Sharing the connection is what makes a transaction spanning several module
/// contexts possible at all — EF refuses to enlist a context in a transaction that belongs to a
/// different connection.
/// </summary>
/// <remarks>
/// Registered scoped, so "a scope" means one request or one consumed message. Nothing opens the
/// connection eagerly: EF opens it on first use and <see cref="UnitOfWork"/> opens it when it
/// starts a transaction.
/// </remarks>
public sealed class GridCoreDbConnection : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsConnection;

    /// <summary>Wraps a connection.</summary>
    /// <param name="connection">The connection every context in this scope will use.</param>
    /// <param name="ownsConnection">
    /// Whether disposing this wrapper disposes the connection. The host owns its connections (so
    /// they return to the Npgsql pool); a test handing in a long-lived SQLite in-memory connection
    /// passes <see langword="false"/>, because closing it would drop the database.
    /// </param>
    public GridCoreDbConnection(DbConnection connection, bool ownsConnection = true)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Connection = connection;
        _ownsConnection = ownsConnection;
    }

    /// <summary>The shared connection.</summary>
    public DbConnection Connection { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsConnection)
        {
            Connection.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection)
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
