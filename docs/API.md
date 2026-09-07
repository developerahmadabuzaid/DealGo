# API Reference

Base path: `/api/v1`. Interactive documentation: `/swagger`.

Authentication is `Authorization: Bearer <accessToken>`. Access tokens last 15 minutes; refresh
tokens rotate on every use and last 30 days.

## Error envelope

Every failure returns the same shape:

```json
{ "code": "AUCTION_NOT_ACTIVE", "message": "The auction is not accepting bids.", "details": [] }
```

Branch on `code`, never on `message`.

| Status | Meaning |
|---|---|
| 400 | A well-formed request the platform's rules decline |
| 401 | Missing, expired or invalid token |
| 403 | Authenticated but not permitted (includes shopping-wallet restrictions) |
| 404 | Not found |
| 409 | Conflict: already joined, duplicate, target already reached, auction full |
| 429 | Rate limited |
| 500 | Unexpected — logged in full, generic message returned |

### Error codes

```text
AUCTION_NOT_FOUND          AUCTION_NOT_ACTIVE        AUCTION_FINISHED
AUCTION_NOT_READY          REGISTRATION_CLOSED       AUCTION_FULL
INVALID_AUCTION_STATE      INVALID_AUCTION_CONFIGURATION

PARTICIPANT_NOT_FOUND      ALREADY_JOINED            ENTRY_PAYMENT_REQUIRED
BID_NOT_ALLOWED            TARGET_ALREADY_REACHED    DUPLICATE_REQUEST
RATE_LIMITED

INSUFFICIENT_BALANCE       INSUFFICIENT_SHOPPING_CREDIT
SHOPPING_WALLET_OPERATION_NOT_ALLOWED
WALLET_NOT_FOUND           CURRENCY_MISMATCH         INVALID_AMOUNT

PRODUCT_NOT_FOUND          PRODUCT_NOT_AVAILABLE     OUT_OF_STOCK
PAYMENT_METHOD_NOT_ACCEPTED  STORE_DISABLED          DUPLICATE_SKU
ORDER_NOT_FOUND            ORDER_NOT_CANCELLABLE     INVALID_ORDER_STATE
ORDER_AMOUNT_OUT_OF_RANGE  CATEGORY_NOT_FOUND

PRIZE_CLAIM_NOT_FOUND      PRIZE_CLAIM_NOT_PAYABLE   PRIZE_CLAIM_NOT_CLAIMABLE

UNAUTHORIZED  FORBIDDEN  INVALID_CREDENTIALS  INVALID_REFRESH_TOKEN
EMAIL_ALREADY_REGISTERED   ACCOUNT_DISABLED   INVALID_OTP
VALIDATION_FAILED  NOT_FOUND  CONFLICT  INTERNAL_ERROR
```

## Pagination

List endpoints accept `page`, `pageSize` (max 200), `search`, `sortBy`, `sortDescending` and return:

```json
{ "items": [], "page": 1, "pageSize": 20, "totalCount": 0, "totalPages": 0,
  "hasPrevious": false, "hasNext": false }
```

---

## Auth

| Method | Path | Notes |
|---|---|---|
| POST | `/auth/register` | Creates the user, both wallets, and returns tokens |
| POST | `/auth/login` | Same error for unknown email and wrong password |
| POST | `/auth/refresh` | Rotates: the presented token is consumed |
| POST | `/auth/logout` | `allDevices` revokes every session |
| POST | `/auth/verify-otp` | |
| POST | `/auth/forgot-password` | Always 200, so it cannot enumerate accounts |
| POST | `/auth/reset-password` | Revokes all sessions |
| POST | `/auth/change-password` | Revokes all sessions |

Presenting an already-consumed refresh token is treated as theft: the whole token family for that
user is revoked.

## Profile

| Method | Path |
|---|---|
| GET | `/me` |
| PUT | `/me` |

## Auctions

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/auctions` | anon | `group` = live / upcoming / completed |
| GET | `/auctions/{id}` | anon | `my*` fields populated when authenticated |
| GET | `/auctions/{id}/snapshot` | anon | **Reconnection payload** |
| GET | `/auctions/{id}/leaderboard` | anon | |
| GET | `/auctions/{id}/winners` | anon | |
| GET | `/auctions/{id}/participants` | user | |
| POST | `/auctions/{id}/join` | user | Charges the entry fee from the Main wallet |
| GET | `/auctions/{id}/my-participation` | user | |
| POST | `/auctions/{id}/bids` | user | Rate limited |

### Snapshot

```json
{
  "id": "auction-id",
  "status": "FinalPhase",
  "currentPrice": 2380.00,
  "startingPrice": 1000.00,
  "targetPrice": 2500.00,
  "finalPhasePrice": 2000.00,
  "bidIncrement": 20.00,
  "currency": "USD",
  "participantCount": 400,
  "requiredParticipants": 400,
  "remainingSeconds": 72,
  "endsAt": "2026-09-07T16:03:00Z",
  "targetReached": false,
  "progress": 0.92,
  "myRank": 3,
  "myHighestPrice": 2340.00,
  "leaderboard": [],
  "lastSequenceNumber": 69,
  "serverTime": "2026-09-07T16:01:48Z"
}
```

`serverTime` lets the client render a countdown without trusting the device clock.

### Placing a bid

```http
POST /api/v1/auctions/{id}/bids
{ "idempotencyKey": "unique-client-generated-id" }
```

That is the entire request. The server determines the previous price, new price, sequence number
and timestamp.

```json
{
  "bidId": "…", "sequenceNumber": 75,
  "previousPrice": 2480.00, "newPrice": 2500.00, "targetPrice": 2500.00,
  "reachedTarget": true, "finalPhase": true, "auctionStatus": "Finished",
  "myRank": 1, "myHighestPrice": 2500.00,
  "serverTimestamp": "…", "wasDuplicate": false
}
```

Retrying with the same key returns the original result with `wasDuplicate: true`.

## Wallet

| Method | Path | Notes |
|---|---|---|
| GET | `/wallet` | Both balances, separately; no total |
| GET | `/wallet/transactions` | |
| GET | `/wallet/main/transactions` | |
| GET | `/wallet/shopping` | |
| GET | `/wallet/shopping/transactions` | |
| POST | `/wallet/deposit` | Main wallet |
| POST | `/wallet/withdraw` | Main wallet only |
| POST | `/wallet/shopping/{withdraw,transfer,convert}` | **Always 403** |

## Store

| Method | Path | Auth |
|---|---|---|
| GET | `/store/home` | anon |
| GET | `/store/products` | anon |
| GET | `/store/products/{id}` | anon |
| GET | `/store/categories` | anon |
| GET | `/store/categories/{id}` | anon |
| POST | `/store/checkout/quote` | user |
| POST | `/store/orders` | user |
| GET | `/store/orders` | user |
| GET | `/store/orders/{id}` | user |
| POST | `/store/orders/{id}/cancel` | user |
| GET/POST | `/store/addresses` | user |
| DELETE | `/store/addresses/{id}` | user |

Checkout requires an `idempotencyKey`; a retry returns the original order.

## Prizes & notifications

| Method | Path |
|---|---|
| GET | `/me/wins` |
| GET | `/me/prize-claims` |
| GET | `/me/prize-claims/{id}` |
| POST | `/prize-claims/{id}/pay` |
| POST | `/prize-claims/{id}/claim` |
| GET | `/notifications` |
| GET | `/notifications/unread-count` |
| POST | `/notifications/{id}/read` |
| POST | `/notifications/read-all` |
| POST/DELETE | `/notifications/devices` |

## Admin

Every admin endpoint is guarded by a permission policy checked on the server.

### Auctions — `Auction.*`

```text
GET    /admin/auctions                    Auction.Read
GET    /admin/auctions/{id}               Auction.Read
POST   /admin/auctions                    Auction.Create
PUT    /admin/auctions/{id}               Auction.Update
DELETE /admin/auctions/{id}               Auction.Cancel
POST   /admin/auctions/{id}/publish       Auction.Update
POST   /admin/auctions/{id}/start         Auction.Start
PATCH  /admin/auctions/{id}/schedule      Auction.Start
POST   /admin/auctions/{id}/pause         Auction.Start
POST   /admin/auctions/{id}/resume        Auction.Start
POST   /admin/auctions/{id}/cancel        Auction.Cancel
GET    /admin/auctions/{id}/participants  Auction.Read
GET    /admin/auctions/{id}/winners       Auction.Read
GET    /admin/auctions/{id}/events        Auction.Read
```

### Prize catalog — `Auction.*`

```text
GET/POST         /admin/catalog/products
GET/PUT/DELETE   /admin/catalog/products/{id}
```

### Store — `Store.*`

```text
GET    /admin/store/overview | /statistics          Store.Read
GET    /admin/store/products                        Store.Read
POST   /admin/store/products                        Store.CreateProduct
PUT    /admin/store/products/{id}                   Store.UpdateProduct
DELETE /admin/store/products/{id}                   Store.DeleteProduct
POST   /admin/store/products/{id}/{activate|hide|archive}   Store.UpdateProduct
GET/POST/PUT/DELETE  /admin/store/categories[/{id}]
GET    /admin/store/inventory                       Store.ManageInventory
POST   /admin/store/inventory/{productId}/adjust    Store.ManageInventory
GET    /admin/store/inventory/history               Store.ManageInventory
GET    /admin/store/orders[/{id}]                   Store.ManageOrders
POST   /admin/store/orders/{id}/{process|ship|deliver|complete|cancel}  Store.ManageOrders
POST   /admin/store/orders/{id}/refund              Wallet.Refund
GET    /admin/store/settings                        Store.Read
PUT    /admin/store/settings                        Store.ManageSettings
```

### Wallets, users, reporting

```text
GET  /admin/wallets                                 Wallet.Read
GET  /admin/wallets/{userId}                        Wallet.Read
GET  /admin/wallets/{userId}/transactions           Wallet.Read
GET  /admin/shopping-credits                        Wallet.Read
GET  /admin/shopping-credits/summary                Wallet.Read
POST /admin/users/{userId}/shopping-credit          Wallet.Adjust
POST /admin/users/{userId}/wallet-adjustment        Wallet.Adjust
GET  /admin/users[/{id}]                            User.Read
PUT  /admin/users/{id}/roles                        SUPER_ADMIN only
PUT  /admin/users/{id}/active                       User.Manage
GET  /admin/dashboard                               Reports.Read
GET  /admin/audit-logs                              Audit.Read
```

## Webhooks

```http
POST /api/v1/webhooks/payments/{provider}
X-Signature: sha256=<hex HMAC-SHA256 of the raw body>
```

Anonymous — the provider cannot hold a user token; authenticity comes from the signature. A
redelivered event returns 200 without moving money again.

## SignalR

Hub: `/hubs/auction`. Group: `auction:{auctionId}`, plus a personal `user:{userId}` group.
Authenticate with `?access_token=<jwt>` (browsers cannot set headers on a WebSocket handshake; the
server accepts the query token only on the hub path).

### Client → server

| Method | Returns |
|---|---|
| `JoinAuction(auctionId)` | The authoritative snapshot |
| `LeaveAuction(auctionId)` | |
| `GetSnapshot(auctionId)` | Explicit resync |
| `GetServerTime()` | For clock-offset correction |

### Server → client

```text
AuctionParticipantCountChanged   AuctionReady
AuctionStartScheduled            AuctionStartTimeChanged
AuctionStarted

BidPlaced                        AuctionPriceChanged
ParticipantRankChanged           LeaderboardUpdated

FinalPhaseStarted
AuctionFinished                  WinnersDeclared

PrizeClaimAvailable              ShoppingCreditAdded
AuctionCancelled                 AuctionPaused           AuctionResumed
```

Every one of these is sent strictly after the owning database transaction committed.

## Health

```text
/health        overall
/health/live   liveness (no dependencies)
/health/ready  readiness (PostgreSQL, Redis)
```
