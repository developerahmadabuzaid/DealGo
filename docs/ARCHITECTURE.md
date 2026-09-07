# Architecture

## Shape

A modular monolith in four layers, with dependencies pointing inward only.

```text
┌──────────────────────────────────────────────────────────────┐
│ AuctionPlatform.Api                                          │
│   Controllers · SignalR hub · background workers · admin SPA │
│   Auth, authorization policies, rate limiting, error envelope│
└───────────────────────────┬──────────────────────────────────┘
                            │
┌───────────────────────────▼──────────────────────────────────┐
│ AuctionPlatform.Infrastructure                               │
│   EF Core / PostgreSQL · Identity · Redis · FCM · webhooks   │
└───────────────────────────┬──────────────────────────────────┘
                            │
┌───────────────────────────▼──────────────────────────────────┐
│ AuctionPlatform.Application                                  │
│   Services, DTOs, orchestration, transactional boundaries    │
│   Abstractions the outer layers implement                    │
└───────────────────────────┬──────────────────────────────────┘
                            │
┌───────────────────────────▼──────────────────────────────────┐
│ AuctionPlatform.Domain                                       │
│   Entities · enums · pricing rules · state machines          │
│   No framework, no I/O                                       │
└──────────────────────────────────────────────────────────────┘
```

Controllers are thin: validate, authorise, delegate, project. No business rule lives in a
controller, and EF entities are never returned over the wire.

### Why `AuctionPlatform.*` and not `Auction.*`

The specification names the projects `Auction.Domain`, `Auction.Api`, and so on. That makes
`Auction` both a namespace root and the central entity class, which the C# compiler reports as
CS0118 ("`Auction` is a namespace but is used like a type") almost everywhere the entity is
referenced. Prefixing the assemblies avoids a workaround on nearly every file. The folder structure
and layering are otherwise exactly as specified.

## The authority rule

PostgreSQL is authoritative for every business fact:

| Fact | Decided by | Never by |
|---|---|---|
| Current price | Bid transaction | Client, cache, SignalR |
| Auction end | Server clock + duration | Device clock |
| Rank | Highest price, then earliest server timestamp | Client ordering |
| Winners | Ranking over committed participant rows | Anything else |
| Balance | Locked wallet row + ledger entry | Client |
| Stock | Locked product row | Client |
| Order total | Server recalculation at checkout | Client |

Redis carries locks, rate-limit counters, caches and the SignalR backplane. Losing Redis degrades
efficiency, not correctness: the database guards still hold.

## Real-time

```text
Bid request ──► BidService
                  │
                  ├─ BEGIN TRANSACTION
                  ├─ SELECT … FOR UPDATE on the auction row
                  ├─ validate against the locked row
                  ├─ write bid, price, participant, events
                  └─ COMMIT
                          │
                          ▼   (only now)
                  SignalR broadcast ──► auction:{id} group
                          │
                          ▼
                  Redis backplane ──► every other API instance
```

Broadcasting happens strictly after commit, so a client never sees a state the database would
disagree with. Broadcast failures are logged, not propagated — the bid is already durable.

### Reconnection

Group membership does not survive a reconnect, and events during the gap are simply gone. Rather
than replay them, the client rejoins its groups and pulls `GET /auctions/{id}/snapshot`, which
returns the authoritative state plus `serverTime`. The client renders its countdown against
`serverTime − deviceTime`, so a wrong device clock cannot make the timer lie.

The mobile client also resyncs on a timer (20s) as a safety net for anything the stream lost.

## Multi-instance behaviour

Every API instance is stateless and runs the same background workers. Three mechanisms keep them
from duplicating work:

1. **Row locks.** Bids, wallet movements, checkout and stock adjustments all take
   `SELECT … FOR UPDATE`, so concurrent work on one row serialises across instances.
2. **Conditional updates.** The scheduler starts an auction with an `UPDATE … WHERE status IN (…)
   AND ActualStartedAt IS NULL`; only one instance can match.
3. **Unique constraints.** Refunds, bids, orders and payments each carry a unique key that makes a
   duplicate physically impossible, whichever instance attempts it.

Redis locks sit on top as an efficiency measure — they stop several instances from *starting* the
same work — but correctness never depends on them.

Verify with `docker compose --profile scale up -d`, which brings up a second API on port 8091.

## Background workers

| Worker | Interval | Responsibility |
|---|---|---|
| `AuctionSchedulerService` | 2s | Start auctions whose scheduled time arrived |
| `AuctionExpiryService` | 2s | Apply the expiration policy when the duration elapses |
| `AuctionCompletionWorker` | 30s | Retry winner declaration and failed refunds |
| `ShoppingCreditExpiryWorker` | 1h | Expire credit when the store is configured to |

The completion worker is what makes the platform self-healing: if an instance dies between a
target-reaching bid and winner declaration, the next sweep finishes the job, and every step is
idempotent so repeating it changes nothing.

## Transactional boundaries

Each of these is exactly one database transaction:

- Join auction: charge entry fee, create participant, bump the counter, schedule the start
- Place bid: lock, validate, write bid, advance price, update participant, maybe finish
- Declare winners: rank, write winners, create prize claims, set participant outcomes
- Refund one participant: insert the refund marker, then credit the wallet
- Checkout: lock products, validate stock, debit wallet, write order, decrement stock
- Adjust inventory: lock product, apply delta, write the inventory transaction

`IApplicationDbContext.BeginTransactionAsync` returns a *nested* handle when a transaction is
already open, whose `Commit` and `Dispose` are no-ops. Without it, an inner service using
`await using` would commit or dispose its caller's transaction.

## Error contract

Everything that fails produces the same envelope:

```json
{ "code": "AUCTION_NOT_ACTIVE", "message": "…", "details": [] }
```

`ExceptionHandlingMiddleware` maps `DomainException` codes to HTTP statuses and logs anything
unexpected in full while returning a generic message. The mobile client branches on `code`, never
on the message text.

## Caching

Only read-heavy, slow-changing data is cached: store categories and catalog listings, invalidated
whenever an admin changes them. Wallet balances, auction prices, winner state and payment state are
never cached.
