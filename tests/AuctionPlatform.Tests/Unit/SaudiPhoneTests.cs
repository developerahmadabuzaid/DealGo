using AuctionPlatform.Domain.Common;
using FluentAssertions;

namespace AuctionPlatform.Tests.Unit;

public class SaudiPhoneTests
{
    [Theory]
    [InlineData("0501234567")]
    [InlineData("050 123 4567")]
    [InlineData("(050) 123-4567")]
    [InlineData("501234567")]
    [InlineData("+966501234567")]
    [InlineData("+966 50 123 4567")]
    [InlineData("966501234567")]
    [InlineData("00966501234567")]
    [InlineData("٠٥٠١٢٣٤٥٦٧")]
    [InlineData("۰۵۰۱۲۳۴۵۶۷")]
    public void Every_way_of_writing_one_number_gives_the_same_canonical_form(string raw)
    {
        SaudiPhone.TryNormalise(raw, out var phone).Should().BeTrue();
        phone.Should().Be("+966501234567");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0112345678")]        // Riyadh landline, not a mobile
    [InlineData("050123456")]         // one digit short
    [InlineData("05012345678")]       // one digit long
    [InlineData("+962791234567")]     // Jordanian mobile
    [InlineData("+44 7911 123456")]   // UK mobile
    [InlineData("1234567")]
    [InlineData("05O1234567")]        // a letter O among the digits
    [InlineData("user@example.com")]
    public void Anything_else_is_refused(string raw)
    {
        SaudiPhone.TryNormalise(raw, out _).Should().BeFalse();

        var normalise = () => SaudiPhone.Normalise(raw);
        normalise.Should().Throw<DomainException>().Where(e => e.Code == ErrorCodes.PhoneNumberInvalid);
    }
}
