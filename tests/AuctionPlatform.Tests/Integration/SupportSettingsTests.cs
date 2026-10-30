using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>The support WhatsApp number: set in the console, read by the app.</summary>
[Collection("database")]
public class SupportSettingsTests
{
    private readonly DatabaseFixture _fixture;

    public SupportSettingsTests(DatabaseFixture fixture) => _fixture = fixture;

    private static ISupportService Support(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ISupportService>();

    [Fact]
    public async Task A_saved_number_is_stored_in_international_form_and_read_back()
    {
        await using (var scope = _fixture.CreateScope())
        {
            var saved = await Support(scope).UpdateAsync(new UpdateSupportSettingsRequest("055 123 4567"));
            saved.WhatsappNumber.Should().Be("+966551234567");
        }

        await using (var scope = _fixture.CreateScope())
        {
            (await Support(scope).GetAsync()).WhatsappNumber.Should().Be("+966551234567");

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.AuditLogs.AnyAsync(a => a.Action == AuditActionType.SupportSettingsChanged))
                .Should().BeTrue();

            // Put the default back for whatever reads it next.
            await Support(scope).UpdateAsync(new UpdateSupportSettingsRequest(SiteContent.DefaultSupportWhatsapp));
        }
    }

    [Fact]
    public async Task A_number_that_is_not_a_saudi_mobile_is_refused()
    {
        await using var scope = _fixture.CreateScope();

        foreach (var bad in new[] { "", "12345", "+44 20 7946 0958", "not a number" })
        {
            var act = () => Support(scope).UpdateAsync(new UpdateSupportSettingsRequest(bad));
            await act.Should().ThrowAsync<DomainException>().Where(e => e.Code == ErrorCodes.PhoneNumberInvalid, bad);
        }
    }

    [Fact]
    public async Task The_default_number_stands_in_until_one_is_set()
    {
        SiteContent.DefaultSupportWhatsapp.Should().Be("+966533851110");

        await using var scope = _fixture.CreateScope();
        (await Support(scope).GetAsync()).WhatsappNumber.Should().StartWith("+9665");
    }
}
