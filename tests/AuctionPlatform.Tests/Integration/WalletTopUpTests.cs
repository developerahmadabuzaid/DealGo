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
    private static (string Body, string Hmac) Callback(
        Guid paymentId, long amountCents, bool success = true, bool pending = false, long transactionId = 0)
    {
        var reference = paymentId.ToString("N");
        var obj = new JsonObject
        {
            ["id"] = transactionId == 0 ? Random.Shared.NextInt64(1, long.MaxValue) : transactionId,
            ["pending"] = pending,
            ["amount_cents"] = amountCents,
            ["success"] = success,
            ["is_auth"] = false,
            ["is_capture"] = false,
            ["is_standalone_payment"] = true,
            ["is_voided"] = false,
            ["is_refunded"] = false,
            ["is_3d_secure"] = true,
            ["integration_id"] = 1001,
            ["has_parent_transaction"] = false,
            ["order"] = new JsonObject
            {
                ["id"] = DatabaseFixture.FakePaymobClient.OrderIdFor(reference),
                ["merchant_order_id"] = reference
            },
            ["created_at"] = "2026-10-30T12:00:00.000000+03:00",
            ["currency"] = "SAR",
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
            result.Succeeded.Should().BeTrue();
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
}
