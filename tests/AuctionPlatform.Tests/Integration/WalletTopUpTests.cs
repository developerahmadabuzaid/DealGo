using System.Text.Json;
using System.Text.Json.Nodes;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Topping up the wallet through Paymob. Money may only arrive from a correctly signed,
/// successful transaction for the exact amount recorded, and only once, however many times and
/// by whichever route (server callback or the customer's return) Paymob reports it.
/// </summary>
[Collection("database")]
public class WalletTopUpTests
{
    private readonly DatabaseFixture _fixture;

    public WalletTopUpTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<(Guid UserId, Guid PaymentId)> StartAsync(decimal amount = 150m)
    {
        Guid userId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await TestData.CreateUserAsync(db, "Payer", mainBalance: 0m)).Id;
        }

        await using var start = _fixture.CreateScope();
        var topUp = await start.ServiceProvider.GetRequiredService<IWalletTopUpService>()
            .StartAsync(userId, amount, "https://site.test");

        topUp.CheckoutUrl.Should().StartWith("https://checkout.test/");
        return (userId, topUp.PaymentId);
    }

    /// <summary>A Paymob "transaction processed" callback for the payment, as Paymob shapes it.</summary>
    internal static (string Body, string Hmac) Callback(
        Guid paymentId, long amountCents, bool success = true, bool pending = false, long transactionId = 0,
        string currency = "SAR", bool voided = false, bool refunded = false, long? orderId = null, string? reference = null)
    {
        var ownReference = paymentId.ToString("N");
        reference ??= ownReference;
        var obj = new JsonObject
        {
            ["id"] = transactionId == 0 ? Random.Shared.NextInt64(1, long.MaxValue) : transactionId,
            ["pending"] = pending,
            ["amount_cents"] = amountCents,
            ["success"] = success,
            ["is_auth"] = false,
            ["is_capture"] = false,
            ["is_standalone_payment"] = true,
            ["is_voided"] = voided,
            ["is_refunded"] = refunded,
            ["is_3d_secure"] = true,
            ["integration_id"] = 1001,
            ["has_parent_transaction"] = false,
            ["order"] = new JsonObject
            {
                ["id"] = orderId ?? DatabaseFixture.FakePaymobClient.OrderIdFor(ownReference),
                ["merchant_order_id"] = reference
            },
            ["created_at"] = "2026-10-30T12:00:00.000000+03:00",
            ["currency"] = currency,
            ["error_occured"] = false,
            ["owner"] = 42,
            ["source_data"] = new JsonObject { ["pan"] = "2346", ["type"] = "card", ["sub_type"] = "MasterCard" },
            ["data"] = new JsonObject { ["message"] = success ? "Approved" : "Do not honour" }
        };

        var body = new JsonObject { ["type"] = "TRANSACTION", ["obj"] = obj }.ToJsonString();
        using var doc = JsonDocument.Parse(body);
        var hmac = PaymobClient.ComputeHmac(doc.RootElement.GetProperty("obj"), DatabaseFixture.PaymobHmacSecret);
        return (body, hmac);
    }

    private async Task<decimal> MainBalanceAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.Wallets.AsNoTracking()
            .FirstAsync(w => w.UserId == userId && w.WalletType == WalletType.Main)).Balance;
    }

    private async Task<WebhookResult> SendAsync(string body, string hmac)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().HandleCallbackAsync(body, hmac);
    }

    [Fact]
    public async Task Starting_a_top_up_moves_no_money()
    {
        var (userId, paymentId) = await StartAsync();

        (await MainBalanceAsync(userId)).Should().Be(0m);

        await using var scope = _fixture.CreateScope();
        var status = await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().GetAsync(userId, paymentId);
        status.Status.Should().Be(PaymentStatus.Processing);
    }

    [Fact]
    public async Task A_signed_successful_payment_credits_the_wallet_exactly_once()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, transactionId: 777001);

        (await SendAsync(body, hmac)).Accepted.Should().BeTrue();
        (await MainBalanceAsync(userId)).Should().Be(150m);

        // Paymob redelivers.
        (await SendAsync(body, hmac)).AlreadyProcessed.Should().BeTrue();

        // And the customer's return carries the same transaction.
        await using (var scope = _fixture.CreateScope())
        {
            var query = ReturnQuery(body);
            var result = await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().HandleReturnAsync(query);
            result.Valid.Should().BeTrue();
            result.Status.Should().Be(PaymentStatus.Completed);
        }

        (await MainBalanceAsync(userId)).Should().Be(150m, "the same payment is credited once");

        await using var check = _fixture.CreateScope();
        var status = await check.ServiceProvider.GetRequiredService<IWalletTopUpService>().GetAsync(userId, paymentId);
        status.Status.Should().Be(PaymentStatus.Completed);
    }

    [Fact]
    public async Task A_callback_with_a_wrong_signature_is_refused()
    {
        var (userId, paymentId) = await StartAsync();
        var (body, _) = Callback(paymentId, 15000);

        var result = await SendAsync(body, new string('0', 128));

        result.Accepted.Should().BeFalse();
        (await MainBalanceAsync(userId)).Should().Be(0m);
    }

    [Fact]
    public async Task A_payment_for_a_different_amount_is_never_credited()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 100);   // one riyal paid for a 150 riyal top-up

        (await SendAsync(body, hmac)).Accepted.Should().BeTrue();

        (await MainBalanceAsync(userId)).Should().Be(0m);
    }

    [Fact]
    public async Task A_declined_payment_marks_the_top_up_failed_and_moves_no_money()
    {
        var (userId, paymentId) = await StartAsync();
        var (body, hmac) = Callback(paymentId, 15000, success: false);

        await SendAsync(body, hmac);

        (await MainBalanceAsync(userId)).Should().Be(0m);
        await using var scope = _fixture.CreateScope();
        var status = await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().GetAsync(userId, paymentId);
        status.Status.Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task A_pending_payment_waits_and_is_credited_when_it_succeeds()
    {
        var (userId, paymentId) = await StartAsync();

        var (pendingBody, pendingHmac) = Callback(paymentId, 15000, pending: true, transactionId: 888001);
        await SendAsync(pendingBody, pendingHmac);
        (await MainBalanceAsync(userId)).Should().Be(0m);

        var (doneBody, doneHmac) = Callback(paymentId, 15000, transactionId: 888001);
        await SendAsync(doneBody, doneHmac);
        (await MainBalanceAsync(userId)).Should().Be(150m);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(50000)]
    public async Task A_top_up_outside_the_limits_is_refused(decimal amount)
    {
        Guid userId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await TestData.CreateUserAsync(db, "Payer")).Id;
        }

        await using var start = _fixture.CreateScope();
        var act = () => start.ServiceProvider.GetRequiredService<IWalletTopUpService>()
            .StartAsync(userId, amount, "https://site.test");

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>The same transaction as Paymob puts it in the return address's query string.</summary>
    private static Dictionary<string, string> ReturnQuery(string callbackBody)
    {
        using var doc = JsonDocument.Parse(callbackBody);
        var t = doc.RootElement.GetProperty("obj");
        string S(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => e.GetString()!,
            _ => e.GetRawText()
        };

        var q = new Dictionary<string, string>
        {
            ["amount_cents"] = S(t.GetProperty("amount_cents")),
            ["created_at"] = S(t.GetProperty("created_at")),
            ["currency"] = S(t.GetProperty("currency")),
            ["error_occured"] = S(t.GetProperty("error_occured")),
            ["has_parent_transaction"] = S(t.GetProperty("has_parent_transaction")),
            ["id"] = S(t.GetProperty("id")),
            ["integration_id"] = S(t.GetProperty("integration_id")),
            ["is_3d_secure"] = S(t.GetProperty("is_3d_secure")),
            ["is_auth"] = S(t.GetProperty("is_auth")),
            ["is_capture"] = S(t.GetProperty("is_capture")),
            ["is_refunded"] = S(t.GetProperty("is_refunded")),
            ["is_standalone_payment"] = S(t.GetProperty("is_standalone_payment")),
            ["is_voided"] = S(t.GetProperty("is_voided")),
            ["order"] = S(t.GetProperty("order").GetProperty("id")),
            ["merchant_order_id"] = S(t.GetProperty("order").GetProperty("merchant_order_id")),
            ["owner"] = S(t.GetProperty("owner")),
            ["pending"] = S(t.GetProperty("pending")),
            ["source_data.pan"] = S(t.GetProperty("source_data").GetProperty("pan")),
            ["source_data.sub_type"] = S(t.GetProperty("source_data").GetProperty("sub_type")),
            ["source_data.type"] = S(t.GetProperty("source_data").GetProperty("type")),
            ["success"] = S(t.GetProperty("success")),
        };
        q["hmac"] = PaymobClient.ComputeHmac(q, DatabaseFixture.PaymobHmacSecret);
        return q;
    }

    // ------------------------------------------------------------------ audit additions

    private async Task<PaymentStatus> StatusAsync(Guid userId, Guid paymentId)
    {
        await using var scope = _fixture.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().GetAsync(userId, paymentId)).Status;
    }

    [Fact]
    public async Task A_payment_in_another_currency_is_never_credited()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, currency: "EGP");

        (await SendAsync(body, hmac)).Accepted.Should().BeTrue();

        (await MainBalanceAsync(userId)).Should().Be(0m);
        (await StatusAsync(userId, paymentId)).Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task A_transaction_naming_another_payment_as_its_reference_is_not_applied()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (_, otherPayment) = await StartAsync(150m);

        // This payment's order, but another payment's reference.
        var (body, hmac) = Callback(paymentId, 15000, reference: otherPayment.ToString("N"));
        await SendAsync(body, hmac);

        (await MainBalanceAsync(userId)).Should().Be(0m);
        (await StatusAsync(userId, paymentId)).Should().Be(PaymentStatus.Processing);
    }

    [Fact]
    public async Task A_transaction_for_an_order_we_never_opened_credits_nobody()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, orderId: 999_999_999_999, reference: Guid.NewGuid().ToString("N"));

        (await SendAsync(body, hmac)).Accepted.Should().BeTrue();

        (await MainBalanceAsync(userId)).Should().Be(0m);
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PaymentWebhookEvents.AsNoTracking()
                .AnyAsync(e => e.ProcessingError != null && e.ProcessingError.StartsWith(WalletTopUpService.NoMatch)))
            .Should().BeTrue("an unmatched confirmation is kept for a person to look at");
    }

    [Theory]
    [InlineData(true, false)]   // voided
    [InlineData(false, true)]   // refunded
    public async Task A_voided_or_refunded_transaction_moves_no_money(bool voided, bool refunded)
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, voided: voided, refunded: refunded);

        await SendAsync(body, hmac);

        (await MainBalanceAsync(userId)).Should().Be(0m);
        (await StatusAsync(userId, paymentId)).Should().Be(PaymentStatus.Failed);
    }

    [Fact]
    public async Task Simultaneous_deliveries_of_the_same_callback_credit_once()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, transactionId: 555_001);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { return await SendAsync(body, hmac); }
            catch { return null; }
        }));

        results.Should().Contain(r => r != null && r.Accepted);
        (await MainBalanceAsync(userId)).Should().Be(150m);
    }

    [Fact]
    public async Task A_callback_and_an_inquiry_reporting_the_same_payment_at_once_credit_once()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, transactionId: 555_002);
        DatabaseFixture.FakePaymobClient.InquiryAnswers[
            DatabaseFixture.FakePaymobClient.OrderIdFor(paymentId.ToString("N"))] =
            JsonDocument.Parse(body).RootElement.GetProperty("obj").GetRawText();

        await BackdateAsync(paymentId, TimeSpan.FromMinutes(10));

        await Task.WhenAll(
            Task.Run(async () => { try { await SendAsync(body, hmac); } catch { } }),
            Task.Run(async () =>
            {
                await using var scope = _fixture.CreateScope();
                try { await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().ReconcileAsync(); } catch { }
            }));

        (await MainBalanceAsync(userId)).Should().Be(150m);
    }

    [Fact]
    public async Task The_customers_return_alone_never_credits_the_wallet()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, _) = Callback(paymentId, 15000);

        // A correctly signed "success" in the browser address, but Paymob's API has no such
        // transaction: nothing may be credited on the browser's word.
        await using (var scope = _fixture.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>()
                .HandleReturnAsync(ReturnQuery(body));
            result.Valid.Should().BeTrue();
            result.Status.Should().Be(PaymentStatus.Processing);
        }

        (await MainBalanceAsync(userId)).Should().Be(0m);
    }

    [Fact]
    public async Task On_return_the_server_asks_Paymob_and_credits_what_Paymob_confirms()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, _) = Callback(paymentId, 15000, transactionId: 555_003);
        DatabaseFixture.FakePaymobClient.InquiryAnswers[
            DatabaseFixture.FakePaymobClient.OrderIdFor(paymentId.ToString("N"))] =
            JsonDocument.Parse(body).RootElement.GetProperty("obj").GetRawText();

        await using (var scope = _fixture.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>()
                .HandleReturnAsync(ReturnQuery(body));
            result.Status.Should().Be(PaymentStatus.Completed);
        }

        (await MainBalanceAsync(userId)).Should().Be(150m);
    }

    [Fact]
    public async Task A_confirmation_that_failed_to_apply_is_applied_on_the_next_delivery()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, transactionId: 555_004);

        // The database fails half way: the event is recorded, the credit is not.
        await using (var broken = _fixture.CreateScope(failWalletCredits: true))
        {
            var act = () => broken.ServiceProvider.GetRequiredService<IWalletTopUpService>().HandleCallbackAsync(body, hmac);
            await act.Should().ThrowAsync<Exception>();
        }
        (await MainBalanceAsync(userId)).Should().Be(0m);

        // Paymob delivers again (it retries a non-200). This time it goes through, once.
        var again = await SendAsync(body, hmac);
        again.AlreadyProcessed.Should().BeFalse("an unapplied confirmation must not be dismissed as a duplicate");
        (await MainBalanceAsync(userId)).Should().Be(150m);
    }

    [Fact]
    public async Task After_a_restart_reconciliation_applies_a_recorded_but_unapplied_confirmation()
    {
        var (userId, paymentId) = await StartAsync(150m);
        var (body, hmac) = Callback(paymentId, 15000, transactionId: 555_005);

        await using (var broken = _fixture.CreateScope(failWalletCredits: true))
        {
            try { await broken.ServiceProvider.GetRequiredService<IWalletTopUpService>().HandleCallbackAsync(body, hmac); }
            catch { /* the crash */ }
        }

        // Paymob never retries; the event has been sitting there.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.PaymentWebhookEvents.Where(e => e.ProviderTransactionId == "555005")
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ReceivedAt, DateTime.UtcNow.AddMinutes(-5)));
            await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().ReconcileAsync();
        }

        (await MainBalanceAsync(userId)).Should().Be(150m);
    }

    [Fact]
    public async Task An_unpaid_top_up_is_closed_once_its_checkout_has_long_expired()
    {
        var (userId, paymentId) = await StartAsync(150m);
        await BackdateAsync(paymentId, TimeSpan.FromHours(3));

        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().ReconcileAsync();
        }

        (await StatusAsync(userId, paymentId)).Should().Be(PaymentStatus.Cancelled);
        (await MainBalanceAsync(userId)).Should().Be(0m);
    }

    [Fact]
    public async Task When_Paymob_does_not_answer_the_top_up_fails_and_moves_no_money()
    {
        Guid userId;
        await using (var scope = _fixture.CreateScope())
        {
            userId = (await TestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), "Payer")).Id;
        }

        DatabaseFixture.FakePaymobClient.FailNextCheckout = true;
        await using (var scope = _fixture.CreateScope())
        {
            var act = () => scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().StartAsync(userId, 100m, "https://site.test");
            (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ServiceUnavailable);
        }

        await using var check = _fixture.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Payments.AsNoTracking().SingleAsync(p => p.UserId == userId)).Status.Should().Be(PaymentStatus.Failed);
        (await MainBalanceAsync(userId)).Should().Be(0m);
    }

    [Fact]
    public async Task One_user_cannot_read_another_users_top_up()
    {
        var (_, paymentId) = await StartAsync(150m);

        await using var scope = _fixture.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<IWalletTopUpService>().GetAsync(Guid.NewGuid(), paymentId);
        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task A_user_cannot_open_unlimited_unpaid_top_ups()
    {
        var (userId, _) = await StartAsync(50m);

        await using var scope = _fixture.CreateScope();
        var topUps = scope.ServiceProvider.GetRequiredService<IWalletTopUpService>();
        for (var i = 0; i < 4; i++) await topUps.StartAsync(userId, 50m, "https://site.test");

        var act = () => topUps.StartAsync(userId, 50m, "https://site.test");
        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task A_rejected_signature_is_recorded_for_monitoring()
    {
        var (_, paymentId) = await StartAsync();
        var (body, _) = Callback(paymentId, 15000);

        await SendAsync(body, "f00d");

        await using var scope = _fixture.CreateScope();
        var health = await scope.ServiceProvider.GetRequiredService<IPaymentMonitorService>().GetHealthAsync();
        health.CallbacksRejected24h.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_wallet_whose_balance_differs_from_its_ledger_is_reported()
    {
        Guid walletId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Balance set with no ledger entry behind it: exactly the inconsistency to catch.
            var user = await TestData.CreateUserAsync(db, "Drifted", mainBalance: 37m);
            walletId = (await db.Wallets.AsNoTracking()
                .FirstAsync(w => w.UserId == user.Id && w.WalletType == WalletType.Main)).Id;
        }

        await using var check = _fixture.CreateScope();
        var health = await check.ServiceProvider.GetRequiredService<IPaymentMonitorService>().GetHealthAsync();
        health.WalletsOutOfBalance.Should().BeGreaterThan(0);
        health.LedgerGuardsInstalled.Should().BeTrue("the migration installs the ledger guards");
    }

    private async Task BackdateAsync(Guid paymentId, TimeSpan by)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Payments.Where(p => p.Id == paymentId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, p => p.CreatedAt - by));
    }
}