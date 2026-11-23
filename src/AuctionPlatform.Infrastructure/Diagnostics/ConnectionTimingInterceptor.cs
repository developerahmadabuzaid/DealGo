using System.Data.Common;
using AuctionPlatform.Application.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AuctionPlatform.Infrastructure.Diagnostics;

/// <summary>
/// Records how long each connection took to open into <see cref="AppMetrics.ConnectionOpenTime"/>.
///
/// The database driver reports how many connections are in use and how long a new one takes to
/// create, but not how long a request waited for one. That wait is what a bidder feels when the
/// pool runs dry, so it is measured here. Observation only: nothing is changed or delayed.
/// </summary>
public sealed class ConnectionTimingInterceptor : DbConnectionInterceptor
{
    public static readonly ConnectionTimingInterceptor Instance = new();

    private ConnectionTimingInterceptor() { }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        AppMetrics.ConnectionOpenTime.Record(eventData.Duration.TotalSeconds);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        AppMetrics.ConnectionOpenTime.Record(eventData.Duration.TotalSeconds);
        return Task.CompletedTask;
    }
}
