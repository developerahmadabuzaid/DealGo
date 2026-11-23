using System.Reflection;
using AuctionPlatform.Api.Controllers.Admin;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Infrastructure.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace AuctionPlatform.Tests.Unit;

public class RuntimeMetricsTests
{
    [Fact]
    public void The_platforms_own_timings_are_collected_and_kept_apart_by_operation()
    {
        using var metrics = new RuntimeMetrics();
        var before = metrics.Snapshot();

        AppMetrics.ConnectionOpenTime.Record(0.004);   // 4 ms
        AppMetrics.RecordLockWait("bid", TimeSpan.FromMilliseconds(12));
        AppMetrics.RecordLockWait("bid", TimeSpan.FromMilliseconds(30));
        AppMetrics.RecordLockWait("join", TimeSpan.FromMilliseconds(2));

        var after = metrics.Snapshot();

        // Other tests in the run record into the same process-wide meter, hence "at least".
        (Count(after, "app.db.connection.open_time") - Count(before, "app.db.connection.open_time"))
            .Should().BeGreaterThanOrEqualTo(1);
        (Count(after, "app.auction.lock_wait{operation=bid}") - Count(before, "app.auction.lock_wait{operation=bid}"))
            .Should().BeGreaterThanOrEqualTo(2);
        (Count(after, "app.auction.lock_wait{operation=join}") - Count(before, "app.auction.lock_wait{operation=join}"))
            .Should().BeGreaterThanOrEqualTo(1);

        // Milliseconds, so a reader never has to guess the unit.
        after.Histograms["app.auction.lock_wait{operation=bid}"].MaxMs.Should().BeGreaterThanOrEqualTo(30);

        after.ProcessorCount.Should().BeGreaterThan(0);
        after.Process.CpuTimeMs.Should().BeGreaterThan(0);
        after.Process.WorkingSetBytes.Should().BeGreaterThan(0);
        after.ThreadPool.Threads.Should().BeGreaterThan(0);
    }

    private static long Count(RuntimeSnapshot snapshot, string key) =>
        snapshot.Histograms.TryGetValue(key, out var h) ? h.Count : 0;

    [Fact]
    public void Percentiles_read_at_most_ten_percent_high_and_never_low()
    {
        var histogram = new LogHistogram();
        for (var ms = 1; ms <= 100; ms++) histogram.Record(ms);

        var snapshot = histogram.Snapshot();

        snapshot.Count.Should().Be(100);
        snapshot.MaxMs.Should().Be(100);
        snapshot.P50Ms.Should().BeInRange(50, 55);
        snapshot.P95Ms.Should().BeInRange(95, 104.5);
        snapshot.P99Ms.Should().BeInRange(99, 108.9);
    }

    [Fact]
    public void Only_a_super_administrator_can_read_the_runtime_diagnostics()
    {
        var attribute = typeof(AdminDiagnosticsController).GetCustomAttribute<AuthorizeAttribute>();

        attribute.Should().NotBeNull();
        attribute!.Roles.Should().Be(Roles.SuperAdmin);
        typeof(AdminDiagnosticsController).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        typeof(AdminDiagnosticsController).GetMethods()
            .Should().NotContain(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null);
    }
}
