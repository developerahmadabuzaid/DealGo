using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Unit;

/// <summary>The M5azn password typed into the dashboard is stored encrypted, readable only with the server's key.</summary>
public class SecretProtectorTests
{
    private static AesSecretProtector With(string jwtSecret, string encryptionKey = "") =>
        new(Options.Create(new M5aznOptions { EncryptionKey = encryptionKey }), Options.Create(new JwtOptions { Secret = jwtSecret }));

    [Fact]
    public void A_secret_comes_back_as_it_went_in_and_is_never_stored_as_typed()
    {
        var protector = With("a-server-secret-of-at-least-32-characters");

        var a = protector.Protect("p@ss wörd");
        var b = protector.Protect("p@ss wörd");

        a.Should().StartWith("v1:").And.NotContain("p@ss");
        a.Should().NotBe(b, "every value gets its own nonce");
        protector.Unprotect(a).Should().Be("p@ss wörd");
    }

    [Fact]
    public void Another_key_or_a_changed_value_reads_nothing()
    {
        var value = With("a-server-secret-of-at-least-32-characters").Protect("secret");

        With("another-server-secret-of-at-least-32-chars").Unprotect(value).Should().BeNull();
        With("a-server-secret-of-at-least-32-characters", encryptionKey: "own-key").Unprotect(value).Should().BeNull();

        var tampered = value[..^4] + (value[^4] == 'A' ? "B" : "A") + value[^3..];
        With("a-server-secret-of-at-least-32-characters").Unprotect(tampered).Should().BeNull();
        With("a-server-secret-of-at-least-32-characters").Unprotect("not protected").Should().BeNull();
    }

    [Fact]
    public void Without_any_key_nothing_is_protected()
    {
        var act = () => With("").Protect("secret");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_server_account_is_used_until_the_dashboard_names_one()
    {
        var protector = With("a-server-secret-of-at-least-32-characters");
        var options = new M5aznOptions { Enabled = false, Email = "server@x.test", Password = "server-pass" };

        var none = M5aznAccountProvider.Resolve(null, options, protector);
        none.Should().BeEquivalentTo(new { Enabled = false, Email = "server@x.test", Password = "server-pass", ProfitPercentage = 70m, Source = M5aznSettingsSource.Server });

        // Saved only the switch and the profit: the server's account still signs in.
        var switchOnly = M5aznAccountProvider.Resolve(new M5aznSettings { Enabled = true, ProfitPercentage = 30m }, options, protector);
        switchOnly.Should().BeEquivalentTo(new { Enabled = true, Email = "server@x.test", Password = "server-pass", ProfitPercentage = 30m, Source = M5aznSettingsSource.Dashboard });
        switchOnly.IsActive.Should().BeTrue();

        var own = M5aznAccountProvider.Resolve(new M5aznSettings
        {
            Enabled = true, Email = "dash@x.test", PasswordProtected = protector.Protect("dash-pass")
        }, options, protector);
        own.Should().BeEquivalentTo(new { Email = "dash@x.test", Password = "dash-pass", PasswordUnreadable = false });

        var unreadable = M5aznAccountProvider.Resolve(new M5aznSettings
        {
            Enabled = true, Email = "dash@x.test", PasswordProtected = With("some-other-secret-of-32-characters!!").Protect("x")
        }, options, protector);
        unreadable.PasswordUnreadable.Should().BeTrue();
        unreadable.IsActive.Should().BeFalse("an unreadable password must never fall back to another account's");
    }
}
