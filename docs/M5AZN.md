# M5azn dropshipping

[M5azn](https://m5azn.com) (مخزن) is a Saudi dropshipping supplier. You pick products from its
catalogue and sell them; when a customer buys one, the order is sent to M5azn, which packs it in
its own warehouse, delivers it, and charges the cost to your M5azn seller balance.

M5azn is **not** a general courier. It can only deliver products that are in its own warehouse,
so DealGo's own stock (ADOMO, the demo catalogue, anything you keep yourself) is still shipped by
you as before. The integration only touches products and prizes you link to an M5azn product.

It is **off** by default. With it off nothing is sent, synced or called, and the app works
exactly as it did.

## What it does

| Part | What happens |
|---|---|
| Store orders | A paid order with at least one linked product, and a delivery address, is sent to M5azn. Only the linked lines are sent; the rest stay DealGo's to ship. |
| Prizes | A paid or claimed prize whose auction prize is linked, with a delivery address, is sent to M5azn as a one-line order. |
| Tracking | Every 30 minutes the server asks M5azn about each order on its way and copies its status, tracking number, tracking link and courier. |
| Order status | Sent → the DealGo order becomes **Processing** (the customer can no longer cancel it). When M5azn delivers *everything* on the order: **Shipped** (with the tracking number) and **Delivered**, with the usual notifications. A mixed order stays Processing for you to finish. A prize becomes **Fulfilled** when delivered. |
| Stock | Every hour the server reads M5azn's recent stock changes and sets the stock of linked store products to M5azn's quantity (an inventory entry "Stock synced from M5azn" is written). A product at 0 shows as out of stock. |
| App | "My orders" and "My wins" show a *Shipping and tracking* box once the order has gone to M5azn: progress, courier, tracking number (copyable) and a *Track shipment* link. |
| Admin | **Store → M5azn**: shipments and their status, the reason for any failure, *Send to M5azn* / *Send again* / *Refresh*, and the product and prize links. The order window also shows its M5azn delivery. |

What it never does on its own: cancel or refund a DealGo order. If M5azn cancels or returns an
order, the shipment shows *Cancelled* with a note, and the decision is yours.

## 1. Switch it on

The M5azn seller account (the email and password you sign in to m5azn.com with) is the only
secret. It goes in configuration, never in the repository or the app.

### On SmarterASP

Add to `deploy/smarterasp/local-secrets.ps1` (git-ignored; never commit it):

```powershell
$M5aznEmail    = '<your M5azn seller email>'
$M5aznPassword = '<your M5azn seller password>'

# Optional, shown with their defaults:
# $M5aznBaseUrl       = 'https://m5azn.com'   # KSA. UAE: https://m5azn.ae, Oman: https://m5azn.om
# $M5aznAutoSubmit    = 'true'                # 'false': paid orders wait for approval on the M5azn page
# $M5aznPaymentMethod = 'card'                # the customer already paid DealGo; 'cod' asks M5azn to collect
# $M5aznStockSync     = 'true'
```

Then rebuild the package:

```powershell
pwsh deploy/smarterasp/publish-smarterasp.ps1 -Zip
```

The script writes `M5azn__Enabled=true`, `M5azn__Email`, `M5azn__Password` (and any optional value
you set) into `web.config`. It refuses to build with only one of the two account values, and says
at the end whether M5azn is on. Without them it adds nothing, and M5azn stays off.

### Anywhere else

Environment variables (or user secrets in development):

| Setting | Default | |
|---|---|---|
| `M5azn__Enabled` | `false` | The feature flag. |
| `M5azn__Email`, `M5azn__Password` | — | The seller account. Required when enabled; the API refuses to start without them. |
| `M5azn__BaseUrl` | `https://m5azn.com` | KSA. `https://m5azn.ae` (UAE), `https://m5azn.om` (Oman). |
| `M5azn__AutoSubmit` | `true` | Send paid orders and prizes without waiting. `false`: they wait as *Awaiting approval*. |
| `M5azn__PaymentMethod` | `card` | `card` (already paid) or `cod` (M5azn collects the total on delivery). |
| `M5azn__LookbackDays` | `3` | Only orders paid this recently are picked up, so switching on never sends old ones. |
| `M5azn__WorkerIntervalSeconds` | `120` | How often the worker looks for new work. |
| `M5azn__StatusSyncMinutes` | `30` | How often an order on its way is asked about. |
| `M5azn__StockSyncEnabled` / `M5azn__StockSyncMinutes` | `true` / `60` | Stock copying. |
| `M5azn__MaxAttempts` | `5` | Attempts before a send is marked failed. |
| `M5azn__Language` / `Website` / `ApiVersion` / `AppVersion` / `Device` | `ar` / `M5AZN` / `1.0` / `1.0` / `Web` | The fixed headers M5azn's API asks for. |

## 2. Link products and prizes

In the admin console, **Store → M5azn**:

1. Find the product in M5azn's catalogue and note its **product id** and the **SKU** of the exact
   variant (size, colour). Both are needed: M5azn's order line carries the two.
2. Type them into the product's row and press **Check**: this only reads M5azn's catalogue (no
   order is placed) and fills in the SKU if it was empty. Then **Save**.
3. Set the DealGo price and stock as usual. With stock sync on, the stock follows M5azn's within
   the hour after M5azn's stock next changes.
4. For a prize, link the prize row of the auction (listed under *Prizes of upcoming auctions*)
   before the auction ends.

To unlink, clear both boxes and save. Linking and unlinking are recorded in the audit log.

## 3. Start carefully

Every order sent costs money at M5azn. For the first days:

1. Set `$M5aznAutoSubmit = 'false'`, so each paid order waits on the M5azn page.
2. Link **one** cheap product, buy it yourself with a real address, and press **Send to M5azn**.
3. Check that the order appears in your M5azn dashboard with the right address, then watch its
   status and tracking arrive in the admin page and the app (press **Refresh** to ask at once).
4. Only then switch automatic sending on.

## How sending stays safe

- **Once only.** A store order or prize gets one shipment (`UNIQUE(SourceType, SourceId)`); the
  worker runs under a lock so two server instances never send the same one.
- **After the money, outside it.** Nothing calls M5azn during checkout or a prize payment. The
  worker picks paid work up afterwards, so a slow M5azn never holds a customer up.
- **Never blindly repeated.** Each answer is sorted by what it means for sending again:
  - *Accepted* — done.
  - *Refused* (4xx, or `success: false`) — marked **Failed** with M5azn's reason; never retried on
    its own. Fix the cause (usually the SKU or the balance) and press **Send again**.
  - *Certainly not sent* (no connection, sign-in failed, 401/403, 429) — retried after 5 min,
    15 min, 1 h, 4 h, then Failed.
  - *Unclear* (timeout, 5xx) — the server first asks M5azn for the order by its reference. Found:
    counted as sent. Not found: retried on the same schedule. If it ends Failed, look for the
    reference in M5azn's orders **before** pressing Send again.
- **The reference.** Each shipment has a number from 100001 upwards, sent as the order's `id`.
  It is shown on the M5azn page; search M5azn's dashboard for it.
- **No cancelling after sending.** Once a send has been attempted, the customer's *Cancel order*
  is refused ("already sent to the supplier"). An order cancelled before that is never sent.
- **The account is never logged.** Neither the password nor the session token appears in logs.
  The order body sent is kept on the shipment for support (it holds the delivery address).

## Webhooks

M5azn's documentation describes no webhooks, so there is nothing to receive and nothing to
verify. Tracking works by asking (`GET /api/orders/{id}`) on the schedule above. If M5azn adds
webhooks later, they would go in `WebhooksController` with a signature check, like Paymob's.

## M5azn's API as used here

From the documentation at <https://m5azn-api.stoplight.io> (project *M5azn Api Documentation*).

| Call | Used for |
|---|---|
| `POST /api/v1/auth/login` `{email, password}` → `data.accessToken` | Signing in. The token is kept in memory until shortly before it expires, and renewed once on a 401. |
| `POST /api/v1/orders` | Sending an order: `id, total, payment_method, cod_amount, order_created_at, platform_name, shipping_info{address, country, country_code, city}, customer_info{first_name, last_name, email, phone}, items[{id, product_id, sku, name, qty_ordered}]`. `order_created_at` is written in Saudi time, `yyyy-MM-dd HH:mm:ss`, as in M5azn's example. |
| `GET /api/orders/{id}` | Status and tracking. Note: documented **without** `/v1`. |
| `GET /api/v1/products/qty-changed?hours=N` | Stock changes. |
| `GET /api/v1/product/details/{id}` | The **Check** button. |

Every call carries `X-Lang`, `X-Website: M5AZN`, `X-Api`, `X-Device`, `X-Version`,
`Accept: application/json` and `Authorization: Bearer <token>`.

### What the documentation does not say, and what the code assumes

Apart from sign-in, M5azn documents no answers. The client reads them loosely, and these are
**assumptions that have not been checked against the real API**:

- Answers use the same envelope as sign-in: `{ status, success, data, message }`.
- The new order's id is in `data` as `order_id`, `orderId`, `order_number` or `id`.
- An order's status is a word in `order_status_name`, `status_name`, `status_text`, `order_status`,
  `shipping_status` or `status` (read in that order). Words are read by meaning, in English and
  Arabic: *cancel/return/ملغي* → Cancelled, *out for delivery/transit* → Shipped,
  *delivered/تم التوصيل* → Delivered, *shipped/شحن* → Shipped, *pending/processing/new/قيد التجهيز* →
  Processing. An unknown word keeps the shipment as it was and is shown as it came.
- Tracking: `tracking_number`, `awb`, `awb_number`, `waybill`, …; link: `tracking_url`,
  `tracking_link`; courier: `shipping_company(_name)`, `carrier`, `courier` (an object's `name`
  is used). These are searched up to three levels deep, never inside lists.
- Order details can be fetched by M5azn's id, or by our reference when M5azn gave none. Whether
  M5azn finds an order by the `id` we sent is unknown; it is what makes an unclear answer safe.
- Whether M5azn rejects a second order with the same `id` is unknown, so the code never relies on it.
- Stock changes are a list of `{product_id | id, sku, qty | quantity | stock}`, or with a
  `variants` list per product.

When the real answers are known, `M5aznClient` (`src/AuctionPlatform.Infrastructure/Services`) and
`M5aznStatusMap` (`src/AuctionPlatform.Application/M5azn`) are the two places to adjust.

## Where the code is

| | |
|---|---|
| `Domain/Entities/M5aznShipment.cs`, `Domain/Enums/M5aznEnums.cs` | The shipment and its statuses. `StoreProduct` and `AuctionPrize` gain `M5aznProductId` and `M5aznSku`. Migration `M5aznDropshipping`. |
| `Application/M5azn/` | `IM5aznClient`, `M5aznFulfilmentService` (pick up, send, follow, stock, admin and customer reads), `M5aznStatusMap`. |
| `Infrastructure/Services/M5aznClient.cs` | The typed `HttpClient`. |
| `Api/BackgroundServices/M5aznWorker.cs` | The schedule. |
| `Api/Controllers/Admin/AdminM5aznController.cs` | `/api/v1/admin/m5azn/...` |
| `Api/Controllers/ShipmentTrackingController.cs` | `GET /api/v1/store/orders/{id}/shipment`, `GET /api/v1/me/prize-claims/{id}/shipment` (204 when not with M5azn). |
| `mobile/lib/features/shipping/` | The app's tracking box. |
| `wwwroot/admin/app.js` (`viewM5azn`) | The admin page. |
| Tests | `Unit/M5aznClientTests.cs` (against a stand-in HTTP server), `Integration/M5aznFulfilmentTests.cs` (real PostgreSQL, stand-in M5azn), `mobile/test/shipment_tracking_test.dart`. None of them reaches m5azn.com. |
