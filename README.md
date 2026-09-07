# Real-Time Auction Platform

A production-grade auction platform where participants pay an entry fee, compete to push a price to
a target, and the top four win prizes. Losing participants get their entry fee back as **shopping
credit**, which can only be spent in the platform's own store — a closed loop.

Built to the specification in [`CLAUDE_CODE_MASTER_PROMPT.md`](CLAUDE_CODE_MASTER_PROMPT.md).

| Component | Technology |
|---|---|
| API | ASP.NET Core 10 Web API |
| Database | PostgreSQL 16 (authoritative for everything) |
| Real-time | ASP.NET Core SignalR + Redis backplane |
| Cache / locks / rate limiting | Redis 7 |
| Auth | JWT + rotating refresh tokens |
| Mobile | Flutter 3.44 (Arabic default, RTL, English supported) |
| Admin | Dependency-free SPA served from the API |
| ORM | Entity Framework Core 10 |
| Logging / tracing | Serilog + OpenTelemetry |
| Containers | Docker Compose |

---

## The one rule everything else follows

> **The database and backend are the authority. The client is only a client.**

The server decides the price, the time, the rank, the winners, every balance, the stock and every
order total. SignalR is a *delivery mechanism* for state PostgreSQL has already committed — never a
source of truth. A client that loses its connection refetches an authoritative snapshot rather than
replaying whatever it missed.

---

## Quick start

Everything, in containers:

```bash
docker compose up -d --build
```

Then:

- API + Swagger — <http://localhost:8090/swagger>
- Admin dashboard — <http://localhost:8090/admin/>
- Health — <http://localhost:8090/health>

Development ports are set in `.env` and avoid the common 5432/6379/8080 defaults.

### Running the API from source

```bash
docker compose up -d postgres redis
dotnet run --project src/AuctionPlatform.Api
```

Migrations are applied and reference data seeded on startup. In non-Production environments demo
accounts are created too (`Database:SeedDevelopmentData`).

| Account | Role |
|---|---|
| `superadmin@auctions.local` | SUPER_ADMIN |
| `admin@auctions.local` | ADMIN |
| `store@auctions.local` | STORE_MANAGER |
| `finance@auctions.local` | FINANCE |
| `user1@auctions.local` … `user12@auctions.local` | USER (500 USD main balance) |

All demo accounts use `Passw0rd!`. They are **never** created when `ASPNETCORE_ENVIRONMENT=Production`.

### Running the mobile app

```bash
cd mobile
flutter pub get
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:5199
```

`10.0.2.2` is how the Android emulator reaches the host. Use `http://localhost:<port>` for iOS
simulator, desktop or web.

> On Windows, `flutter run` on a device needs Developer Mode enabled for plugin symlinks
> (`start ms-settings:developers`). `flutter test`, `flutter analyze` and `flutter build web` work
> without it.

---

## Repository layout

```text
├── src/
│   ├── AuctionPlatform.Domain/          Entities, enums, business rules, state machines
│   ├── AuctionPlatform.Application/     Services, DTOs, orchestration, abstractions
│   ├── AuctionPlatform.Infrastructure/  EF Core, Identity, Redis, notifications, webhooks
│   └── AuctionPlatform.Api/             Controllers, SignalR hub, workers, wwwroot/admin
├── tests/AuctionPlatform.Tests/         Unit + integration + concurrency tests
├── mobile/                              Flutter client
├── docs/                                Architecture, API, engine, database, wallets, …
├── docker-compose.yml
└── .github/workflows/ci.yml
```

The layers depend inward only: `Api → Infrastructure → Application → Domain`.

> **Naming note.** The specification suggests `Auction.Domain`, `Auction.Api`, and so on. The
> assemblies here are `AuctionPlatform.*` instead, because a root namespace of `Auction` collides
> with the `Auction` entity class and makes `Auction` ambiguous throughout the codebase (C# CS0118).
> The structure is otherwise exactly as specified.

---

## Documentation

| Document | Covers |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Layers, multi-instance behaviour, real-time design |
| [AUCTION_ENGINE.md](docs/AUCTION_ENGINE.md) | Lifecycle, bid transaction, ranking, refunds |
| [DATABASE.md](docs/DATABASE.md) | Schema, constraints, indexes, migrations |
| [API.md](docs/API.md) | Every endpoint, error codes, SignalR events |
| [WALLETS.md](docs/WALLETS.md) | The two-wallet model and its restrictions |
| [STORE.md](docs/STORE.md) | Catalog, inventory, checkout, orders |
| [PAYMENTS.md](docs/PAYMENTS.md) | Payments, webhooks, idempotency |
| [SECURITY.md](docs/SECURITY.md) | Auth, roles, permissions, rate limits, auditing |
| [DEPLOYMENT.md](docs/DEPLOYMENT.md) | Docker, configuration, scaling, backups |

---

## Tests

```bash
dotnet test                    # 68 tests: unit, integration, concurrency
cd mobile && flutter test      # 17 tests: models, formatting, localization
```

The integration tests start a throwaway PostgreSQL container (Testcontainers) because the
guarantees under test — `SELECT … FOR UPDATE` and unique indexes — only a real database provides.

The specification's critical scenarios are all covered:

| Scenario | Test |
|---|---|
| 100 simultaneous bids at 2480 → exactly one reaches 2500 | `BidConcurrencyTests` |
| Concurrent bids keep a gapless, unique sequence | `BidConcurrencyTests` |
| Retried bid with one idempotency key creates one bid | `BidConcurrencyTests` |
| 30 credit, two 25 debits → one succeeds, balance 5 | `WalletConcurrencyTests` |
| Ledger always reconciles to the balance | `WalletConcurrencyTests` |
| Stock 1, two buyers → one order, one OUT_OF_STOCK | `StoreConcurrencyTests` |
| Retried checkout creates one order | `StoreConcurrencyTests` |
| Repeated completion refunds each loser exactly once | `AuctionCompletionTests` |
| Losers refunded to Shopping, winners not refunded | `AuctionCompletionTests` |
| Shopping wallet withdrawal is refused | `WalletRulesTests` |

---

## How an auction actually runs

```text
Admin creates auction with exactly 4 ranked prizes
        ↓  publish (validated: prices, ranks, participants, duration)
Registration opens
        ↓  users pay the entry fee from their Main wallet
Participant #400 joins
        ↓  ParticipantsCompletedAt recorded, start scheduled
READY / WAITING_FOR_START     ← admin may start now or move the time
        ↓
LIVE  — each bid moves the price by one increment
        ↓  price crosses the final-phase price
FINAL_PHASE — UI turns urgent, timer always visible
        ↓  a bid reaches the target price
FINISHED  → winners ranked → prize claims created (payment still owed)
        ↓
Losers' entry fees credited to their Shopping wallets, exactly once
        ↓
Store purchases spend that credit
```

The bid increment moves the *auction price*. It is not charged to the bidder; the entry fee is the
only payment to take part.
