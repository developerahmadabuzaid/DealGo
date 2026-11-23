using Npgsql;

namespace AuctionPlatform.Infrastructure.Persistence;

/// <summary>
/// Connection pool settings for a database that is far away and slow to connect to.
///
/// On the shared host, opening a fresh connection to PostgreSQL sometimes takes several seconds.
/// The pool used to empty itself whenever traffic paused, so the first bids of an auction paid
/// for new connections while holding the auction's row lock, and everyone behind them waited.
/// These settings keep a few connections open and alive. Anything already written into the
/// connection string wins, so a deployment can still choose its own values.
/// </summary>
public static class PostgresConnection
{
    public static string Tune(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var given = connectionString.ToLowerInvariant();

        bool Has(params string[] keys) => keys.Any(k => given.Contains(k));

        // Connections ready before the next bid needs one.
        if (!Has("minimum pool size", "minpoolsize", "min pool size"))
            builder.MinPoolSize = 5;

        // A quiet minute between auctions no longer closes them all.
        if (!Has("connection idle lifetime", "connectionidlelifetime"))
            builder.ConnectionIdleLifetime = 900;

        // Stops the host's network from silently dropping a connection that is only waiting.
        if (!Has("keepalive"))
            builder.KeepAlive = 30;

        // Saves a round trip each time a connection comes back from the pool. Nothing here sets
        // session state that would need resetting.
        if (!Has("no reset on close", "noresetonclose"))
            builder.NoResetOnClose = true;

        return builder.ConnectionString;
    }
}
