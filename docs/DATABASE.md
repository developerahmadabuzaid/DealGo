# Database

PostgreSQL 16, accessed through EF Core 10. PostgreSQL is authoritative for every business fact;
Redis holds nothing that cannot be rebuilt.

## Conventions

- **Money** — every `decimal` property is mapped to `numeric(18,2)` by a model-wide convention.
  Floating point is never used for currency.
- **Enums** — stored as `int` for compact, stable indexes.
- **Timestamps** — `timestamp with time zone`, always UTC.
- **Concurrency** — `Auctions`, `AuctionParticipants`, `Wallets`, `StoreProducts`, `StoreOrders`
  and `PrizeClaims` carry a concurrency token mapped to PostgreSQL's `xmin` system column.
  Because `xmin` is a system column, `SELECT *` does not return it — locking reads therefore say
  `SELECT *, xmin FROM … FOR UPDATE`.
- **Sequences** — `AuctionNumberSeq` and `OrderNumberSeq` back the `AUC-2026-000001` and
  `ORD-2026-000001` identifiers, so they stay unique across API instances. The primary key remains
  the internal `uuid`.

## Constraints that carry business meaning

These are not incidental; each one is a guarantee the application relies on.

| Constraint | Guarantees |
|---|---|
| `UNIQUE(AuctionPrizes.AuctionId, Rank)` | One prize per rank; exactly four per published auction |
| `UNIQUE(AuctionParticipants.AuctionId, UserId)` | A user cannot join the same auction twice |
| `UNIQUE(Bids.AuctionId, SequenceNumber)` | No two bids share an ordinal |
| `UNIQUE(Bids.AuctionId, ParticipantId, IdempotencyKey)` | A retried bid resolves to the original |
| `UNIQUE(Winners.AuctionId, Rank)` | One winner per rank |
| `UNIQUE(Winners.AuctionId, ParticipantId)` | A participant wins at most once |
| `UNIQUE(AuctionEntryFeeRefunds.AuctionId, ParticipantId)` | **Exactly one refund per participant** |
| `UNIQUE(Wallets.UserId, WalletType)` | Exactly one Main and one Shopping wallet |
| `CHECK(Wallets.Balance >= 0)` | A balance can never go negative |
| `UNIQUE(WalletTransactions.UserId, IdempotencyKey)` *(filtered)* | A financial command moves money once |
| `UNIQUE(Payments.Provider, ProviderTransactionId)` *(filtered)* | A redelivered webhook cannot double-credit |
| `UNIQUE(PaymentWebhookEvents.Provider, ProviderEventId)` | Redelivery is detectable |
| `UNIQUE(StoreProducts.Sku)` | SKUs are unique |
| `UNIQUE(StoreOrders.OrderNumber)` | Order numbers are unique |
| `UNIQUE(StoreOrders.UserId, IdempotencyKey)` *(filtered)* | A retried checkout returns the original order |
| `CHECK(StoreProducts.StockQuantity >= 0)` | Stock can never go negative |

## Tables

### Auctions

Prices, percentages, participant thresholds, timing and status. Both the percentage *and* the
computed price are stored (`TargetPercentage` + `TargetPrice`, `FinalPhasePercentage` +
`FinalPhasePrice`) so historical auctions never shift when the catalog changes.
`MainProductNameSnapshot` preserves the product name for the same reason.

`LastSequenceNumber` is the monotonic bid counter, advanced inside the bid transaction.

Indexes: `Status`, `ScheduledStartAt`, `FinishedAt`, `(Status, ScheduledStartAt)` — the last serves
the scheduler's query directly — `CreatedAt`, and unique `AuctionNumber`.

### AuctionPrizes

Exactly four per published auction, ranks 1–4. Rank 1 is the main product. Carries
`OriginalPrice`, `ProductNameSnapshot` and `ProductImageSnapshot`.

### AuctionParticipants

`HighestPrice` and `HighestPriceAt` are the ranking keys. The composite index
`(AuctionId, HighestPrice, HighestPriceAt)` serves the ranking query.

### Bids

Immutable once written. Records `PreviousPrice`, `NewPrice`, `SequenceNumber`, `IdempotencyKey`,
`ReachedTarget` and `ServerTimestamp` — the authoritative clock reading, never a client value.

### Winners / PrizeClaims

`PrizeClaim.WinnerId` owns the relationship. `Winner.PrizeClaimId` is a convenience pointer and is
deliberately **not** a foreign key: a second FK in the opposite direction makes the two rows
uninsertable in any order (EF reports a circular dependency).

### Wallets / WalletTransactions

The wallet holds the balance; the transaction table is an append-only ledger recording
`BalanceBefore` and `BalanceAfter` for every movement, so the ledger can be reconciled against the
balance.

### Payments / PaymentWebhookEvents

`Payments` records money movements including internal wallet transfers (`Provider = "WALLET"`).
`PaymentWebhookEvents` stores the raw provider payload *before* processing, so redelivery is
detected by a unique-index violation rather than by application logic.

### Store

`StoreCategories`, `StoreProducts`, `StoreProductImages`, `InventoryTransactions`, `StoreOrders`,
`StoreOrderItems`, `ShippingAddresses`, `StoreSettings` (single row).

`StoreOrderItems` stores `ProductNameSnapshot`, `ProductSkuSnapshot`, `ProductImageSnapshot` and
`UnitPrice`, so changing a product's price or name never rewrites an existing order. The FK to
`StoreProducts` is `Restrict`: a product referenced by an order is archived, never deleted.

`InventoryTransactions` is append-only; stock only ever moves alongside a row here.

### Audit

`AuctionEvents` is the per-auction trail (`PARTICIPANT_JOINED`, `REQUIRED_PARTICIPANTS_REACHED`,
`AUCTION_STARTED`, `BID_PLACED`, `TARGET_REACHED`, `WINNERS_DECLARED`, `ENTRY_FEE_REFUNDED`, …).

`AuditLogs` is the administrative trail: action, actor, entity, before/after JSON, reason, IP and
user agent. Both are append-only, and ordinary admins cannot delete from either.

### Identity

ASP.NET Core Identity with `Guid` keys, plus `RefreshTokens` (SHA-256 hashes only — the raw token
is never stored) and `UserDeviceTokens` for push.

## Indexes

Beyond the unique constraints:

```text
Auctions              Status · ScheduledStartAt · FinishedAt · (Status, ScheduledStartAt) · CreatedAt
AuctionParticipants   AuctionId · UserId · (AuctionId, HighestPrice, HighestPriceAt)
Bids                  AuctionId · (AuctionId, ServerTimestamp)
Winners               UserId
PrizeClaims           UserId · Status · (UserId, Status)
AuctionEvents         (AuctionId, Timestamp) · EventType
Wallets               UserId
WalletTransactions    UserId · (UserId, WalletType, CreatedAt) · ReferenceId · CreatedAt
Payments              UserId · Status · CreatedAt
StoreProducts         Status · CategoryId · (Status, IsFeatured) · Slug · CreatedAt
StoreOrders           UserId · Status · CreatedAt
StoreOrderItems       OrderId · ProductId
InventoryTransactions (ProductId, CreatedAt) · ReferenceId
Notifications         UserId · (UserId, ReadAt) · CreatedAt
AuditLogs             CreatedAt · Action · (EntityType, EntityId) · ActorUserId
```

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project src/AuctionPlatform.Infrastructure \
  --startup-project src/AuctionPlatform.Api \
  --output-dir Persistence/Migrations

dotnet ef database update \
  --project src/AuctionPlatform.Infrastructure \
  --startup-project src/AuctionPlatform.Api

dotnet ef migrations script --idempotent --output migrate.sql \
  --project src/AuctionPlatform.Infrastructure \
  --startup-project src/AuctionPlatform.Api
```

The API applies migrations on startup when `Database:AutoMigrate` is true. For production, generate
the idempotent script and apply it through your release process instead, with `AutoMigrate` false —
and set it false on every instance but one if you do let the app migrate.

## Seeding

`SeedEssentialsAsync` creates roles and the store-settings row. It runs everywhere, because the
application cannot work without them.

`SeedDevelopmentDataAsync` creates demo users, catalog products, store products and two sample
auctions. It uses a well-known password and is **never** run when
`ASPNETCORE_ENVIRONMENT=Production`.

## Concurrency strategy

| Operation | Mechanism |
|---|---|
| Place bid | `SELECT … FOR UPDATE` on the auction row |
| Join auction | `SELECT … FOR UPDATE` on the auction row (participant counter) |
| Wallet movement | `SELECT … FOR UPDATE` on the wallet row |
| Checkout | `SELECT … FOR UPDATE` on product rows, ordered by id to avoid deadlock |
| Stock adjustment | `SELECT … FOR UPDATE` on the product row |
| Declare winners | `FOR UPDATE` + the `WinnersDeclared` flag read under that lock |
| Refund | Insert the unique marker before moving money |
| Scheduled start | Conditional `UPDATE … WHERE status … AND ActualStartedAt IS NULL` |
| Admin edits | `xmin` optimistic concurrency |

Locking reads are ordered consistently (products by id, wallets by type) so concurrent operations
cannot deadlock against each other.

`EnableRetryOnFailure` is deliberately **not** configured: Npgsql's retrying execution strategy
refuses to wrap user-initiated transactions, and every critical path here is one. Transient-failure
recovery is the caller's job instead, which is safe because every mutating operation is
idempotency-keyed.
