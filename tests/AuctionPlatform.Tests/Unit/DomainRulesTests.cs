using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using FluentAssertions;

namespace AuctionPlatform.Tests.Unit;

public class AuctionPricingTests
{
    [Fact]
    public void Target_price_is_a_percentage_of_the_original_price()
    {
        AuctionRules.CalculateTargetPrice(5000m, 50m).Should().Be(2500m);
        AuctionRules.CalculateFinalPhasePrice(5000m, 40m).Should().Be(2000m);
    }

    [Theory]
    [InlineData(1000, 20, 2500, 1020)]
    [InlineData(2460, 20, 2500, 2480)]
    [InlineData(2480, 20, 2500, 2500)]
    public void Each_bid_advances_the_price_by_one_increment(
        decimal current, decimal increment, decimal target, decimal expected)
    {
        AuctionRules.NextPrice(current, increment, target).Should().Be(expected);
    }

    [Theory]
    // Closed at 2500 on a 5000 value: half price for every prize, not only the first.
    [InlineData(5000, 2500, 5000, 2500)]
    [InlineData(1200, 2500, 5000, 600)]
    // Closed below target because time ran out: a bigger discount.
    [InlineData(1200, 2000, 5000, 480)]
    // Rounded to money.
    [InlineData(999, 2500, 5000, 499.5)]
    // No usable value or price: full price, never a division by zero.
    [InlineData(1200, 2500, 0, 1200)]
    [InlineData(1200, 0, 5000, 1200)]
    public void Every_prize_gets_the_auction_discount(
        decimal prizePrice, decimal finalPrice, decimal auctionValue, decimal expected)
    {
        AuctionRules.PrizePaymentAmount(prizePrice, finalPrice, auctionValue).Should().Be(expected);
    }

    [Fact]
    public void A_bid_never_overshoots_the_target()
    {
        // 2490 + 20 would be 2510; the auction stops exactly at the target instead.
        AuctionRules.NextPrice(2490m, 20m, 2500m).Should().Be(2500m);
    }

    [Fact]
    public void Reaching_the_target_ends_the_auction_and_2480_does_not()
    {
        AuctionRules.ReachesTarget(2500m, 2500m).Should().BeTrue();
        AuctionRules.ReachesTarget(2480m, 2500m).Should().BeFalse();
    }

    [Fact]
    public void Final_phase_starts_at_the_final_phase_price()
    {
        AuctionRules.EntersFinalPhase(1980m, 2000m).Should().BeFalse();
        AuctionRules.EntersFinalPhase(2000m, 2000m).Should().BeTrue();
    }

    [Fact]
    public void Progress_is_measured_from_the_starting_price_and_clamped()
    {
        AuctionRules.Progress(1000m, 1000m, 2500m).Should().Be(0m);
        AuctionRules.Progress(1000m, 2500m, 2500m).Should().Be(1m);
        AuctionRules.Progress(1000m, 1750m, 2500m).Should().Be(0.5m);
        AuctionRules.Progress(1000m, 9999m, 2500m).Should().Be(1m);
    }

    [Fact]
    public void Money_rounds_half_away_from_zero_to_two_places()
    {
        Money.Round(10.005m).Should().Be(10.01m);
        Money.Round(10.004m).Should().Be(10.00m);
        Money.Round(-10.005m).Should().Be(-10.01m);
    }
}

public class AuctionPublishValidationTests
{
    private static Domain.Entities.Auction ValidAuction()
    {
        var auction = new Domain.Entities.Auction
        {
            Title = "Test auction",
            // The rank 1 prize name is what publish validation requires, not a catalog link.
            MainProductNameSnapshot = "Test prize",
            OriginalPrice = 5000m,
            StartingPrice = 1000m,
            TargetPercentage = 50m,
            FinalPhasePercentage = 40m,
            BidIncrement = 20m,
            EntryFee = 20m,
            RequiredParticipants = 400,
            DurationSeconds = 180,
            StartDelayAfterParticipantsSeconds = 7200
        };
        auction.TargetPrice = AuctionRules.CalculateTargetPrice(5000m, 50m);
        auction.FinalPhasePrice = AuctionRules.CalculateFinalPhasePrice(5000m, 40m);
        return auction;
    }

    [Fact]
    public void A_correctly_configured_auction_passes()
    {
        AuctionRules.ValidateForPublish(ValidAuction(), 4, new[] { 1, 2, 3, 4 })
            .Should().BeEmpty();
    }

    [Fact]
    public void An_auction_must_have_exactly_four_prizes()
    {
        AuctionRules.ValidateForPublish(ValidAuction(), 3, new[] { 1, 2, 3 })
            .Should().Contain(e => e.Contains("exactly 4 prizes"));
    }

    [Fact]
    public void Prize_ranks_must_be_one_through_four_without_duplicates()
    {
        AuctionRules.ValidateForPublish(ValidAuction(), 4, new[] { 1, 2, 2, 4 })
            .Should().Contain(e => e.Contains("Prize ranks"));
    }

    [Fact]
    public void Starting_price_must_be_below_the_target()
    {
        var auction = ValidAuction();
        auction.StartingPrice = 3000m;

        AuctionRules.ValidateForPublish(auction, 4, new[] { 1, 2, 3, 4 })
            .Should().Contain(e => e.Contains("StartingPrice must be less than TargetPrice"));
    }

    [Fact]
    public void Target_price_must_be_below_the_original_price()
    {
        var auction = ValidAuction();
        auction.TargetPercentage = 120m;
        auction.TargetPrice = AuctionRules.CalculateTargetPrice(auction.OriginalPrice, 120m);

        AuctionRules.ValidateForPublish(auction, 4, new[] { 1, 2, 3, 4 })
            .Should().Contain(e => e.Contains("TargetPrice must be less than OriginalPrice"));
    }

    [Fact]
    public void Publishing_an_invalid_auction_throws_with_all_the_reasons()
    {
        var auction = ValidAuction();
        auction.BidIncrement = 0m;
        auction.RequiredParticipants = 0;

        var act = () => AuctionRules.EnsurePublishable(auction, 2, new[] { 1, 2 });

        act.Should().Throw<DomainException>()
            .Where(e => e.Code == ErrorCodes.InvalidAuctionConfiguration)
            .Which.Details.Should().HaveCountGreaterThan(2);
    }
}

public class AuctionStateMachineTests
{
    [Theory]
    [InlineData(AuctionStatus.Draft, AuctionStatus.RegistrationOpen)]
    [InlineData(AuctionStatus.RegistrationOpen, AuctionStatus.WaitingForParticipants)]
    [InlineData(AuctionStatus.WaitingForParticipants, AuctionStatus.Ready)]
    [InlineData(AuctionStatus.Ready, AuctionStatus.Live)]
    [InlineData(AuctionStatus.WaitingForStart, AuctionStatus.Live)]
    [InlineData(AuctionStatus.Live, AuctionStatus.FinalPhase)]
    [InlineData(AuctionStatus.FinalPhase, AuctionStatus.Finished)]
    [InlineData(AuctionStatus.Finished, AuctionStatus.WinnersDeclared)]
    [InlineData(AuctionStatus.WinnersDeclared, AuctionStatus.PrizeClaim)]
    [InlineData(AuctionStatus.PrizeClaim, AuctionStatus.Completed)]
    public void The_happy_path_transitions_are_allowed(AuctionStatus from, AuctionStatus to)
    {
        AuctionStateMachine.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(AuctionStatus.Draft, AuctionStatus.Live)]
    [InlineData(AuctionStatus.RegistrationOpen, AuctionStatus.Live)]
    [InlineData(AuctionStatus.Finished, AuctionStatus.Live)]
    [InlineData(AuctionStatus.Completed, AuctionStatus.Live)]
    [InlineData(AuctionStatus.Cancelled, AuctionStatus.Live)]
    public void Skipping_ahead_or_reviving_a_closed_auction_is_refused(AuctionStatus from, AuctionStatus to)
    {
        AuctionStateMachine.CanTransition(from, to).Should().BeFalse();

        var act = () => AuctionStateMachine.EnsureCanTransition(from, to);
        act.Should().Throw<DomainException>().Where(e => e.Code == ErrorCodes.InvalidAuctionState);
    }

    [Fact]
    public void Bids_are_accepted_only_while_live_or_in_the_final_phase()
    {
        AuctionStateMachine.AcceptsBids(AuctionStatus.Live).Should().BeTrue();
        AuctionStateMachine.AcceptsBids(AuctionStatus.FinalPhase).Should().BeTrue();

        foreach (var status in Enum.GetValues<AuctionStatus>()
                     .Where(s => s != AuctionStatus.Live && s != AuctionStatus.FinalPhase))
        {
            AuctionStateMachine.AcceptsBids(status).Should().BeFalse($"{status} must not accept bids");
        }
    }

    [Fact]
    public void Registration_closes_once_the_participant_target_is_met()
    {
        AuctionStateMachine.AcceptsRegistration(AuctionStatus.RegistrationOpen).Should().BeTrue();
        AuctionStateMachine.AcceptsRegistration(AuctionStatus.WaitingForParticipants).Should().BeTrue();
        AuctionStateMachine.AcceptsRegistration(AuctionStatus.Ready).Should().BeFalse();
        AuctionStateMachine.AcceptsRegistration(AuctionStatus.Live).Should().BeFalse();
    }
}

public class RankingRulesTests
{
    private static AuctionParticipant P(decimal highest, DateTime? at, int bids = 1) => new()
    {
        HighestPrice = highest,
        HighestPriceAt = at,
        BidCount = bids,
        JoinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void The_highest_price_ranks_first()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var ranked = RankingRules.AssignRanks(new[]
        {
            P(2400m, t), P(2500m, t), P(2300m, t)
        });

        ranked[0].HighestPrice.Should().Be(2500m);
        ranked[0].CurrentRank.Should().Be(1);
        ranked[2].HighestPrice.Should().Be(2300m);
    }

    [Fact]
    public void Equal_prices_are_broken_by_the_earlier_server_timestamp()
    {
        // Spec example: A and B both reached 2400; A got there 1.1s earlier and outranks B.
        var a = P(2400m, new DateTime(2026, 1, 1, 12, 1, 20, 100, DateTimeKind.Utc));
        var b = P(2400m, new DateTime(2026, 1, 1, 12, 1, 21, 200, DateTimeKind.Utc));

        var ranked = RankingRules.AssignRanks(new[] { b, a });

        ranked[0].Should().BeSameAs(a);
        ranked[1].Should().BeSameAs(b);
    }

    [Fact]
    public void Exactly_four_winners_are_selected_when_enough_people_bid()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var winners = RankingRules.SelectWinners(new[]
        {
            P(2500m, t), P(2480m, t.AddSeconds(1)), P(2460m, t.AddSeconds(2)),
            P(2440m, t.AddSeconds(3)), P(2420m, t.AddSeconds(4)), P(2400m, t.AddSeconds(5))
        });

        winners.Should().HaveCount(4);
        winners.Select(w => w.HighestPrice).Should().ContainInOrder(2500m, 2480m, 2460m, 2440m);
    }

    [Fact]
    public void Participants_who_never_bid_cannot_win()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var winners = RankingRules.SelectWinners(new[]
        {
            P(2500m, t), P(0m, null, bids: 0), P(0m, null, bids: 0)
        });

        winners.Should().HaveCount(1);
    }
}

public class WalletRulesTests
{
    [Fact]
    public void Only_the_main_wallet_is_withdrawable()
    {
        WalletRules.IsWithdrawable(WalletType.Main).Should().BeTrue();
        WalletRules.IsWithdrawable(WalletType.Shopping).Should().BeFalse();
    }

    [Fact]
    public void Shopping_credit_can_never_be_transferred_or_converted()
    {
        WalletRules.IsTransferable(WalletType.Shopping).Should().BeFalse();
        WalletRules.IsConvertible(WalletType.Shopping).Should().BeFalse();
    }

    [Fact]
    public void Withdrawing_shopping_credit_is_refused_with_the_documented_code()
    {
        var act = () => WalletRules.EnsureWithdrawable(WalletType.Shopping);

        act.Should().Throw<DomainException>()
            .Where(e => e.Code == ErrorCodes.ShoppingWalletOperationNotAllowed);
    }

    [Fact]
    public void The_withdrawable_total_excludes_shopping_credit()
    {
        // Main 150 + Shopping 40 must never be presented as 190 withdrawable.
        WalletRules.WithdrawableBalance(150m).Should().Be(150m);
    }

    [Fact]
    public void Insufficient_funds_report_a_wallet_specific_code()
    {
        var main = () => WalletRules.EnsureSufficientBalance(10m, 20m, WalletType.Main);
        main.Should().Throw<DomainException>().Where(e => e.Code == ErrorCodes.InsufficientBalance);

        var shopping = () => WalletRules.EnsureSufficientBalance(10m, 20m, WalletType.Shopping);
        shopping.Should().Throw<DomainException>().Where(e => e.Code == ErrorCodes.InsufficientShoppingCredit);
    }

    [Theory]
    [InlineData(StorePaymentMode.ShoppingWallet, StorePaymentMethod.ShoppingWallet, true)]
    [InlineData(StorePaymentMode.ShoppingWallet, StorePaymentMethod.MainWallet, false)]
    [InlineData(StorePaymentMode.MainWallet, StorePaymentMethod.ShoppingWallet, false)]
    [InlineData(StorePaymentMode.Both, StorePaymentMethod.ShoppingWallet, true)]
    [InlineData(StorePaymentMode.Both, StorePaymentMethod.MainWallet, true)]
    public void A_product_only_accepts_the_wallets_it_is_configured_for(
        StorePaymentMode mode, StorePaymentMethod method, bool expected)
    {
        WalletRules.AcceptsPayment(mode, method).Should().Be(expected);
    }
}

public class StoreOrderStateMachineTests
{
    [Theory]
    [InlineData(StoreOrderStatus.Paid, StoreOrderStatus.Processing)]
    [InlineData(StoreOrderStatus.Processing, StoreOrderStatus.Shipped)]
    [InlineData(StoreOrderStatus.Shipped, StoreOrderStatus.Delivered)]
    [InlineData(StoreOrderStatus.Delivered, StoreOrderStatus.Completed)]
    [InlineData(StoreOrderStatus.Paid, StoreOrderStatus.Cancelled)]
    public void The_documented_fulfilment_path_is_allowed(StoreOrderStatus from, StoreOrderStatus to)
    {
        StoreOrderStateMachine.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(StoreOrderStatus.Shipped, StoreOrderStatus.Cancelled)]
    [InlineData(StoreOrderStatus.Delivered, StoreOrderStatus.Processing)]
    [InlineData(StoreOrderStatus.Refunded, StoreOrderStatus.Paid)]
    public void Moving_backwards_or_cancelling_a_shipped_order_is_refused(
        StoreOrderStatus from, StoreOrderStatus to)
    {
        StoreOrderStateMachine.CanTransition(from, to).Should().BeFalse();
    }

    [Fact]
    public void A_customer_can_only_cancel_before_the_order_ships()
    {
        StoreOrderStateMachine.IsCustomerCancellable(StoreOrderStatus.Paid).Should().BeTrue();
        StoreOrderStateMachine.IsCustomerCancellable(StoreOrderStatus.Processing).Should().BeTrue();
        StoreOrderStateMachine.IsCustomerCancellable(StoreOrderStatus.Shipped).Should().BeFalse();
        StoreOrderStateMachine.IsCustomerCancellable(StoreOrderStatus.Delivered).Should().BeFalse();
    }
}
