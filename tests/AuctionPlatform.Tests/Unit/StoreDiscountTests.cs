using AuctionPlatform.Application.Store;
using FluentAssertions;

namespace AuctionPlatform.Tests.Unit;

public class StoreDiscountTests
{
    [Theory]
    [InlineData(75, 100, 25)]
    [InlineData(60, 100, 40)]
    [InlineData(99.99, 100, 0)]   // rounded down: not yet a whole percent
    [InlineData(1, 3, 66)]
    public void The_discount_is_the_gap_between_the_two_prices(
        decimal price, decimal compareAt, int expected)
    {
        StoreDiscount.Applies(price, compareAt).Should().BeTrue();
        StoreDiscount.Percentage(price, compareAt).Should().Be(expected);
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(100, 100d)]       // the same price is not an offer
    [InlineData(100, 80d)]        // "before" below the price is not an offer
    [InlineData(100, 0d)]
    // A nullable decimal cannot be written inline, so the "before" price arrives as a double.
    public void Anything_that_is_not_a_real_saving_is_not_an_offer(decimal price, double? compareAt)
    {
        var was = compareAt is null ? (decimal?)null : (decimal)compareAt.Value;

        StoreDiscount.Applies(price, was).Should().BeFalse();
        StoreDiscount.Percentage(price, was).Should().Be(0);
    }
}
