using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Npgsql;

namespace AuctionPlatform.Tests.Unit;

public class PostgresConnectionTests
{
    [Fact]
    public void A_plain_connection_string_keeps_connections_warm()
    {
        var tuned = new NpgsqlConnectionStringBuilder(
            PostgresConnection.Tune("Host=db;Database=a;Username=u;Password=p"));

        tuned.MinPoolSize.Should().Be(5);
        tuned.ConnectionIdleLifetime.Should().Be(900);
        tuned.KeepAlive.Should().Be(30);
        tuned.NoResetOnClose.Should().BeTrue();
        tuned.Host.Should().Be("db");
        tuned.Password.Should().Be("p");
    }

    [Fact]
    public void Values_written_into_the_connection_string_win()
    {
        var tuned = new NpgsqlConnectionStringBuilder(
            PostgresConnection.Tune("Host=db;Minimum Pool Size=2;Keepalive=0;Connection Idle Lifetime=60"));

        tuned.MinPoolSize.Should().Be(2);
        tuned.KeepAlive.Should().Be(0);
        tuned.ConnectionIdleLifetime.Should().Be(60);
    }
}
