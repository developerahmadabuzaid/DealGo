using System.Net;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// Where Paymob sends the customer after the checkout page. The result arrives signed in the
/// query string, so it is applied exactly as the server callback would be (whichever arrives
/// first credits the wallet; the other changes nothing). The page itself only says what happened
/// and leads back to the wallet.
/// </summary>
[ApiController]
[Route("api/v1/payments")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class PaymentsReturnController : ControllerBase
{
    private readonly IWalletTopUpService _topUps;

    public PaymentsReturnController(IWalletTopUpService topUps) => _topUps = topUps;

    [HttpGet("paymob/return")]
    public async Task<ContentResult> PaymobReturn(CancellationToken ct)
    {
        var query = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
        var result = await _topUps.HandleReturnAsync(query, ct);

        var (title, body, color) = result switch
        {
            { Valid: true, Succeeded: true } =>
                ("تمت عملية الدفع", "أُضيف المبلغ إلى محفظتك. يمكنك العودة إلى التطبيق الآن.", "#1F9D55"),
            { Valid: true } =>
                ("لم تكتمل عملية الدفع", "لم يُخصم أي مبلغ لهذه المحاولة. يمكنك المحاولة مرة أخرى من المحفظة.", "#C0392B"),
            _ =>
                ("تعذّر التحقق من الدفع", "إن خُصم المبلغ فسيظهر في محفظتك خلال دقائق.", "#B7791F"),
        };

        var html = $$"""
            <!doctype html>
            <html lang="ar" dir="rtl">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{WebUtility.HtmlEncode(title)}} · DealGo</title>
              <style>
                body { margin: 0; min-height: 100vh; display: grid; place-items: center;
                       background: #F5F6FA; font-family: system-ui, -apple-system, "Segoe UI", Tahoma, sans-serif; }
                .card { background: #fff; border-radius: 16px; padding: 32px 24px; max-width: 360px; margin: 16px;
                        text-align: center; box-shadow: 0 6px 24px rgba(0,0,0,.08); }
                h1 { color: {{color}}; font-size: 22px; margin: 0 0 12px; }
                p { color: #444; line-height: 1.7; margin: 0 0 24px; }
                a { display: inline-block; background: #C2410C; color: #fff; text-decoration: none;
                    padding: 12px 28px; border-radius: 10px; font-weight: 700; }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p>{{WebUtility.HtmlEncode(body)}}</p>
                <a href="/app/#/wallet">العودة إلى المحفظة</a>
              </div>
            </body>
            </html>
            """;

        return Content(html, "text/html; charset=utf-8");
    }
}
