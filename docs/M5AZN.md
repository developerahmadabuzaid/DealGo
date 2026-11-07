# M5azn dropshipping

[M5azn](https://m5azn.com) (مخزن) is a Saudi dropshipping supplier. You pick products from its
catalogue and sell them; when a customer buys one, the order is sent to M5azn, which packs it in
its own warehouse, delivers it, and charges the cost to your M5azn seller balance.

**Store only.** M5azn is used for the store and nothing else: auctions, their prizes and
anything won in an auction are never sent to M5azn and are delivered by you as before.

M5azn is **not** a general courier. It can only deliver products that are in its own warehouse.
So the store has two kinds of product, kept apart:

- **Imported from M5azn**: the products you added to your shop on m5azn.com. DealGo imports them
  (name, description, every picture, category, price, stock) and keeps them in step. M5azn ships them.
- **Added in the dashboard** (ADOMO, the demo catalogue, anything you keep yourself): never shown on
  the M5azn page, never sent to M5azn. You ship them as before.

It is **off** by default. With it off nothing is sent, synced or called, and the app works
exactly as it did.

## What it does

| Part | What happens |
|---|---|
| Products | **Import from M5azn** (and every hour on its own) reads your M5azn shop's product list and creates a store product for each, with every picture, the description, the category (made if the store has none of that name), the stock and the price. Importing again updates them by M5azn's product id and never duplicates them. A product no longer in the list is **hidden**, never deleted, and shown again if it comes back. |
| Store orders | A paid order with at least one imported product, and a delivery address, is sent to M5azn. Only those lines are sent; the rest stay DealGo's to ship. |
| Tracking | Every 30 minutes the server asks M5azn about each order on its way and copies its status, tracking number, tracking link and courier. |
| Order status | Sent → the DealGo order becomes **Processing** (the customer can no longer cancel it). When M5azn delivers *everything* on the order: **Shipped** (with the tracking number) and **Delivered**, with the usual notifications. A mixed order stays Processing for you to finish. |
| Prices | The DealGo price of an imported product is **M5azn's price plus a profit percentage**, 70% unless you change it: a product M5azn sells you for 100 SAR is priced 170 SAR. Set on every import, and for every imported product at once when you change the percentage. |
| Stock | Every import sets the stock to M5azn's, and every hour the server also reads M5azn's recent stock changes (an inventory entry is written for each change). A product at 0 shows as out of stock. |
| App | The product page shows the main picture with a thumbnail of every picture under it (for every product, not only M5azn's), the SKU, the available stock and the delivery time. "My orders" shows a *Shipping and tracking* box once the order has gone to M5azn: progress, courier, tracking number (copyable) and a *Track shipment* link. |
| Admin | **Store → M5azn**: the on/off switch, the seller account, the profit percentage and the delivery time; the imported products and the last import's result; shipments and their status, the reason for any failure, *Send to M5azn* / *Send again* / *Refresh*. The order window also shows its M5azn delivery. |

What it never does on its own: cancel or refund a DealGo order. If M5azn cancels or returns an
order, the shipment shows *Cancelled* with a note, and the decision is yours.

## 1. Switch it on

### From the dashboard (the easy way)

**Store → M5azn → M5azn account and pricing**:

1. *M5azn*: **Enabled**.
2. *Seller email* and *Seller password*: the account you sign in to m5azn.com with.
3. *M5azn address*: m5azn.com (KSA), m5azn.ae (UAE) or m5azn.om (Oman).
4. *Automatic sending*: start with **Disabled** (see *Start carefully* below).
5. *Profit %*: 70 by default.
6. **Save**, then **Check sign-in**. It only signs in; nothing is ordered.

Only staff allowed to change the store settings see this card. The password is stored encrypted
and is never shown again or sent back to the browser: leave the box empty to keep it, type a new
one to change it. Changing the email needs the password of that account. Emptying the email goes
back to the account in the server settings, if any.

The password is encrypted with a key derived from the server's `Jwt:Secret`, or from
`M5azn:EncryptionKey` when that is set. If that secret changes, the page says the password can no
longer be read, M5azn stops, and you type the password again. Nothing else is lost.

Once saved on the dashboard, the dashboard's switch, account, address and automatic sending win
over the server settings below. Every server picks a change up within half a minute; the worker
acts on it within two minutes.

### From the server settings

The account can also be set without the dashboard. It is a secret: it goes in configuration,
never in the repository or the app.

#### On SmarterASP

Add to `deploy/smarterasp/local-secrets.ps1` (git-ignored; never commit it):

```powershell
$M5aznEmail    = '<your M5azn seller email>'
$M5aznPassword = '<your M5azn seller password>'

# Optional, shown with their defaults:
# $M5aznBaseUrl       = 'https://m5azn.com'   # KSA. UAE: https://m5azn.ae, Oman: https://m5azn.om
# $M5aznAutoSubmit    = 'true'                # 'false': paid orders wait for approval on the M5azn page
# $M5aznPaymentMethod = 'card'                # the customer already paid DealGo; 'cod' asks M5azn to collect
# $M5aznStockSync     = 'true'
# $M5aznEncryptionKey = '<long random text>'  # encrypts the password saved on the dashboard
```

Then rebuild the package:

```powershell
pwsh deploy/smarterasp/publish-smarterasp.ps1 -Zip
```

The script writes `M5azn__Enabled=true`, `M5azn__Email`, `M5azn__Password` (and any optional value
you set) into `web.config`. It refuses to build with only one of the two account values, and says
at the end whether M5azn is on. Without them it adds nothing: M5azn stays off until it is switched
on from the dashboard.

#### Anywhere else

Environment variables (or user secrets in development):

| Setting | Default | |
|---|---|---|
| `M5azn__Enabled` | `false` | The feature flag. |
| `M5azn__Email`, `M5azn__Password` | — | The seller account, unless it is entered on the dashboard. Set both or neither: the API refuses to start with only one. |
| `M5azn__EncryptionKey` | — | Optional key for the password saved on the dashboard. Without it, one is derived from `Jwt__Secret`. |
| `M5azn__BaseUrl` | `https://m5azn.com` | KSA. `https://m5azn.ae` (UAE), `https://m5azn.om` (Oman). |
| `M5azn__AutoSubmit` | `true` | Send paid store orders without waiting. `false`: they wait as *Awaiting approval*. |
| `M5azn__PaymentMethod` | `card` | `card` (already paid) or `cod` (M5azn collects the total on delivery). |
| `M5azn__LookbackDays` | `3` | Only orders paid this recently are picked up, so switching on never sends old ones. |
| `M5azn__WorkerIntervalSeconds` | `120` | How often the worker looks for new work. |
| `M5azn__StatusSyncMinutes` | `30` | How often an order on its way is asked about. |
| `M5azn__StockSyncEnabled` / `M5azn__StockSyncMinutes` | `true` / `60` | Stock copying. |
| `M5azn__MaxAttempts` | `5` | Attempts before a send is marked failed. |
| `M5azn__Language` / `Website` / `ApiVersion` / `AppVersion` / `Device` | `ar` / `M5AZN` / `1.0` / `1.0` / `Web` | The fixed headers M5azn's API asks for. |

## 2. Import your M5azn products

1. On m5azn.com, add the products you want to sell to your shop (*أضف إلى متجري*). The ones you
   added show as *مضاف مسبقاً*.
2. On **Store → M5azn**, press **Import from M5azn**. It only reads your product list (nothing is
   ordered) and says what it did: found, new, updated, hidden, skipped, and any problem in words.
3. The products appear in the store at once, under M5azn's categories, priced at M5azn's price
   plus the profit percentage. After that the import runs every hour on its own.

What an import never does: touch a product added in the dashboard, delete anything, or show
again a product you set to *draft* yourself.

A product is **skipped**, and the result says so, when M5azn gives it no SKU (M5azn needs one on
every order) or no price (nothing is sold without one). If M5azn's answer as a whole cannot be
read, nothing at all is changed: an unreadable answer is never taken for an empty shop.

### Categories

Each imported product goes into the store category matching its M5azn category. The store
categories are made the first time, with M5azn's Arabic names and pictures; a sub-category goes
under its main category (the store has two levels). An existing store category with the same name
is used rather than duplicated.

M5azn's product list does not name the category, so the import reads M5azn's category list
(`GET /api/v1/catalog/categories`) and:

1. uses the category id a product carries, when it carries one;
2. otherwise asks the product list one category at a time (`?category_id=`): main categories first,
   then the sub-categories of those that had products, so the most specific one wins.

Before trusting step 2 it asks for a category that cannot exist: if products still come back,
M5azn ignores the filter, nothing is guessed, and the import says so. Products already in a store
category are not asked about again, so the hourly sync stays light. The questions are paced (one a
second, `M5azn__CatalogRequestDelayMs`), stop as soon as every product has a category, and a
"too many requests" answer (429) is waited out and asked again; if M5azn still refuses, what was
sorted is kept and the rest are asked about on the next import. A category you change by hand on
a product stays as long as M5azn names none.

### Prices and profit

- **The rule**: DealGo price = M5azn price × (1 + profit % ÷ 100), rounded to the halala, where the
  profit % is the product's own rate when it has one, else the general one.
- **A product's own rate**: in the products table, *Own profit %* gives one product a rate of its
  own, which replaces the general one for that product only. Leave it empty (the box shows the
  general rate in grey) to use the general rate again. Saving re-prices the product at once.
- **Changing the general percentage** re-prices at once every imported product that has no rate of
  its own, from the M5azn price last read. M5azn is not asked. Products with their own rate keep it.
- **Every import** reads M5azn's price again, so a price change at M5azn reaches DealGo within the
  hour.
- A price typed on the product page for an imported product is overwritten by the next import.
  Offers (*compare at* prices) are not touched.

### Delivery time

The product page shows a delivery time. For imported products it is M5azn's when M5azn gives one,
else the *Delivery time* set on the M5azn page (**1-5 أيام** by default). Products added in the
dashboard show none.

### Products linked by hand before

Earlier versions let you link a dashboard product to an M5azn product by hand. That is gone: the
migration `M5aznProductImport` clears any such link, so those products are DealGo's to ship again.
There should have been none in production. An order for one that was still waiting to be sent is
cancelled at M5azn's end of the queue, never sent.

## 3. Start carefully

Every order sent costs money at M5azn. For the first days:

1. Set *Automatic sending* to **Disabled** on the M5azn page (or `$M5aznAutoSubmit = 'false'`),
   so each paid order waits there.
2. Import, buy **one** cheap imported product yourself with a real address, and press **Send to M5azn**.
3. Check that the order appears in your M5azn dashboard with the right address, then watch its
   status and tracking arrive in the admin page and the app (press **Refresh** to ask at once).
4. Only then switch automatic sending on.

## How sending stays safe

- **Once only.** A store order gets one shipment (`UNIQUE(SourceType, SourceId)`); the
  worker runs under a lock so two server instances never send the same one.
- **After the money, outside it.** Nothing calls M5azn during checkout. The
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
- **The account is never logged.** Neither the password nor the session token appears in logs
  or in the audit log (which records only that the password was changed).
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
| `GET /api/v1/seller/myproductlist?page=N` | The import: the products in your M5azn shop. |
| `GET /api/v1/catalog/categories` | The import: M5azn's categories, for each product's category. |
| `GET /api/v1/product/details/{id}` | The import, to fill in pictures and the description when the list has too few. |

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
- **The product list** (`seller/myproductlist`) is the first list of objects with an id found in
  the answer (`data`, `data.data`, `products`, `items`, ...). A row may carry the product inside it
  (`{ id, seller_price, product: { id, ... } }`): the product's own id is used, and fields are read
  from the row first, then the product. Paging follows `last_page`, `total_pages`, `next_page_url`
  or `links.next`; without any of them one page is read. Only the default `product_type` (local)
  is asked for.
- Product fields: id `product_id`/`id`; SKU `sku`/`product_sku`/`barcode` (else the first
  variant's); name `name`/`title`/`product_name`/`name_ar`; description `description`/
  `long_description`/`details`/... (HTML becomes plain text); pictures `image`/`main_image`/
  `thumbnail` and the lists `images`/`gallery`/`product_images`/`photos`/`media` (strings or objects
  with `url`/`image`/`src`/`path`; a path is made absolute against M5azn's address); category
  `category_name`/`category` (an object's `name`)/`categories[0]`; delivery time `delivery_time`/
  `shipping_time`/`delivery_days`/....
- The price used for pricing is the first of `seller_price`, `wholesale_price`, `cost_price`,
  `price`, `sale_price`: what the seller pays M5azn is assumed to be one of the first three.
  **Check it on the first import**: the Arencia vitamin C serum (SKU 8809562192770) should come in
  at the 35.15 SAR M5azn shows, so 59.76 SAR at 70%.
- A product with variants (sizes, colours) is imported once, with its own SKU or its first
  variant's; variants are not separate products yet.
- Stock changes are a list of `{product_id | id, sku, qty | quantity | stock}`, or with a
  `variants` list per product.

When the real answers are known, `M5aznClient` (`src/AuctionPlatform.Infrastructure/Services`) and
`M5aznStatusMap` (`src/AuctionPlatform.Application/M5azn`) are the two places to adjust.

## Where the code is

| | |
|---|---|
| `Domain/Entities/M5aznShipment.cs`, `Domain/Enums/M5aznEnums.cs` | The shipment and its statuses. `StoreProduct` gains `M5aznProductId` and `M5aznSku`. Migration `M5aznDropshipping`. |
| `Domain/Entities/M5aznSettings.cs` | The dashboard's settings row. `StoreProduct.M5aznCostPrice` keeps M5azn's price. Migration `M5aznSettingsAndProfit`. |
| `StoreProduct.Source`, `DeliveryTime`, `M5aznSyncedAt`, `M5aznRemovedAt` | Imported or added in the dashboard, and the import's bookkeeping. Migration `M5aznProductImport`. |
| `Application/M5azn/M5aznCatalogService.cs` | The import, the imported products list, re-pricing. |
| `Application/M5azn/` | `IM5aznClient`, `M5aznFulfilmentService` (pick up, send, follow, stock, prices, settings, admin and customer reads), `M5aznStatusMap`, `M5aznAccount` (the settings in effect and the pricing rule). |
| `Infrastructure/Services/M5aznAccountProvider.cs`, `AesSecretProtector.cs` | Which settings are in effect, and the password's encryption. |
| `Infrastructure/Services/M5aznClient.cs` | The typed `HttpClient`. |
| `Api/BackgroundServices/M5aznWorker.cs` | The schedule. |
| `Api/Controllers/Admin/AdminM5aznController.cs` | `/api/v1/admin/m5azn/...` |
| `Api/Controllers/ShipmentTrackingController.cs` | `GET /api/v1/store/orders/{id}/shipment` (204 when not with M5azn). |
| `mobile/lib/features/shipping/` | The app's tracking box. |
| `wwwroot/admin/app.js` (`viewM5azn`) | The admin page. |
| Tests | `Unit/M5aznClientTests.cs` (against a stand-in HTTP server), `Unit/SecretProtectorTests.cs`, `Integration/M5aznFulfilmentTests.cs` (real PostgreSQL, stand-in M5azn), `mobile/test/shipment_tracking_test.dart`. None of them reaches m5azn.com. |
