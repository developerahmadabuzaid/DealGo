# Auction Engine

The most safety-critical part of the platform. Everything here runs server-side and is decided
against committed database state.

## Pricing

An auction is defined by an original price and two percentages, both computed once and **persisted**:

```text
TargetPrice     = OriginalPrice × TargetPercentage     / 100
FinalPhasePrice = OriginalPrice × FinalPhasePercentage / 100
```

With the defaults:

```text
OriginalPrice   5000
TargetPercentage  50%  →  TargetPrice     2500
FinalPhasePercentage 40% → FinalPhasePrice 2000
StartingPrice   1000
BidIncrement      20
```

Prices are stored rather than derived, so editing the catalog product later cannot retroactively
change a running or historical auction. `AuctionPrize` and `Auction` also keep name and image
snapshots for the same reason.

### The bid increment is not a charge

A bid moves the auction price by one increment. It costs the bidder nothing. The entry fee is the
only money a participant pays to take part.

```text
2460 → 2480 → 2500 (target reached, auction ends)
```

`AuctionRules.NextPrice` clamps to the target, so a bid can never overshoot it.

## Publish validation

`AuctionRules.ValidateForPublish` returns *all* violations at once so the admin UI can show them
together. An auction cannot be published unless:

```text
StartingPrice   > 0
BidIncrement    > 0
EntryFee        ≥ 0
RequiredParticipants ≥ 1
DurationSeconds > 0
StartingPrice   < TargetPrice
TargetPrice     < OriginalPrice
FinalPhasePrice < TargetPrice
Exactly 4 prizes, ranks {1,2,3,4} each used once
Title and main product present
```

## State machine

```text
DRAFT ──► SCHEDULED ──► REGISTRATION_OPEN ──► WAITING_FOR_PARTICIPANTS
                                                      │
                                                      ▼
                                           READY / WAITING_FOR_START
                                                      │
                                                      ▼
                                                    LIVE
                                                      │
                                                      ▼
                                                FINAL_PHASE
                                                      │
                                                      ▼
                                                  FINISHED
                                                      │
                                                      ▼
                                            WINNERS_DECLARED
                                                      │
                                                      ▼
                                              PRIZE_CLAIM ──► COMPLETED

PAUSED ◄──► LIVE / FINAL_PHASE / WAITING_FOR_START
CANCELLED, EXPIRED reachable from the appropriate states
```

`AuctionStateMachine` is the only place transitions are authorised. Clients never name a target
state; they invoke an operation and the server decides whether the resulting transition is legal.
Bids are accepted only in `Live` and `FinalPhase`.

## The participant threshold

An auction does not start because it was created. It becomes ready only when

```text
ParticipantCount >= RequiredParticipants
```

The join transaction locks the auction row, so the participant counter is unambiguous even when
hundreds of people join at once — participant #400 is exactly one person. When the threshold is
crossed:

```text
ParticipantsCompletedAt = now
ScheduledStartAt        = now + StartDelayAfterParticipantsSeconds
Status                  = WAITING_FOR_START   (or READY when the delay is zero)
```

Everyone registered is notified. An administrator can then start immediately or move the time; both
are audited and broadcast.

## The bid transaction

This is the heart of the engine. It rests on one thing: **every bid for an auction takes a
row-level lock on that auction inside a transaction**, so bids on the same auction serialise no
matter how many API instances are running. An in-memory lock would only protect one process and is
never used.

```text
── fast path ──────────────────────────────────────────────
  find participant                     → PARTICIPANT_NOT_FOUND
  look for an existing bid with the same idempotency key
      found → return the original result, WasDuplicate = true
  rate limit (Redis, per user per auction)   → RATE_LIMITED

── transaction ────────────────────────────────────────────
  BEGIN
  SELECT *, xmin FROM "Auctions" WHERE "Id" = @id FOR UPDATE

  status must be LIVE or FINAL_PHASE   → AUCTION_NOT_ACTIVE / AUCTION_FINISHED
  TargetReached must be false          → TARGET_ALREADY_REACHED
  now < EndsAt                         → AUCTION_FINISHED

  previousPrice = auction.CurrentPrice
  newPrice      = min(previousPrice + BidIncrement, TargetPrice)
  sequence      = auction.LastSequenceNumber + 1

  INSERT bid (sequence, previousPrice, newPrice, serverTimestamp)
  auction.CurrentPrice = newPrice
  auction.LastSequenceNumber = sequence

  if newPrice >= FinalPhasePrice and status = LIVE:
      status = FINAL_PHASE, FinalPhaseStartedAt = now

  if newPrice >= TargetPrice:
      TargetReached = true, status = FINISHED, FinishedAt = now

  participant.CurrentPrice = newPrice
  participant.BidCount    += 1
  participant.LastBidAt    = now
  if newPrice > participant.HighestPrice:
      participant.HighestPrice   = newPrice
      participant.HighestPriceAt = now      ← the ranking tie-break

  INSERT auction events (BID_PLACED, FINAL_PHASE_STARTED, TARGET_REACHED, …)
  COMMIT

── after commit ───────────────────────────────────────────
  broadcast BidPlaced / AuctionPriceChanged / LeaderboardUpdated
  if finished: run completion (winners, claims, refunds)
```

`xmin` appears in the `SELECT` because it is a PostgreSQL *system* column: `SELECT *` does not
return it, and the EF concurrency token would otherwise be missing.

### Guarantees

**Exactly one bid reaches the target.** At 2480 with a 20 increment and a 2500 target, a hundred
simultaneous bids produce exactly one accepted bid; the other 99 get `AUCTION_FINISHED` or
`TARGET_ALREADY_REACHED`. Verified in `BidConcurrencyTests`.

**Sequence numbers are unique and gapless.** `UNIQUE(AuctionId, SequenceNumber)` backs it up, and
each bid's `PreviousPrice` chains onto the last bid's `NewPrice` with no lost updates.

**Retries are free.** `UNIQUE(AuctionId, ParticipantId, IdempotencyKey)` means a repeated request —
sequential or concurrent — returns the original bid rather than creating a second one.

**A completion failure never looks like a bid failure.** The bid is committed before completion
runs; if completion throws, it is logged and the completion worker retries. Telling the bidder
their bid failed when it did not would be worse than a delayed winner announcement.

## Duration and expiration

The duration starts at `AUCTION_STARTED`, not at creation. `EndsAt = ActualStartedAt + Duration`.
If the target is reached first, the auction finishes immediately.

When the duration elapses without the target, `AuctionExpiryService` applies the configured policy:

| Policy | Effect |
|---|---|
| `EndAndDistribute` *(default)* | Finish, rank whoever bid, award prizes, refund losers |
| `Extend` | Add `ExtensionSeconds` and continue |
| `Cancel` | Cancel the auction |
| `Restart` | Reset to the starting price and restart the clock |

## Ranking

```text
1. Highest price the participant reached      (descending)
2. Earliest server timestamp at which they reached it   (ascending)
3. Join time, then id — deterministic tie-breakers
```

Device timestamps are never consulted. From the specification's example:

```text
A → 2400 at 12:01:20.100
B → 2400 at 12:01:21.200      →  A outranks B
```

Only participants who actually bid can win. An auction with two bidders awards two prizes; the rest
are treated as losers and refunded like any other loser.

## Winners and prize claims

Winner declaration happens in one transaction, guarded by `Auction.WinnersDeclared` read under the
row lock, so it can only happen once:

```text
Rank 1 → Prize 1 (the main product)
Rank 2 → Prize 2
Rank 3 → Prize 3
Rank 4 → Prize 4
```

Each winner gets a `PrizeClaim` in `PaymentRequired` with `PaymentAmount` = the prize's snapshot
value and a deadline of `PrizeClaimPaymentDays`. **Winning grants the right to the prize, not the
prize itself** — the value is still owed, and it is paid from the Main wallet only.

## Entry fee refunds

After winners are declared, each losing participant's entry fee becomes **shopping credit**:

```text
Entry fee  ──►  Shopping wallet   (not Main; not withdrawable)
```

Configurable per auction:

```text
RefundLosingParticipantsEntryFee = true
RefundWinnerEntryFee             = false
RefundDestinationWallet          = Shopping
```

Refunds run one participant at a time in independent transactions, so one failure cannot block the
rest. Each begins by inserting the `AuctionEntryFeeRefund` marker — **before** moving any money —
so `UNIQUE(AuctionId, ParticipantId)` makes a concurrent duplicate fail at the insert rather than
after paying out.

Running completion a hundred times, sequentially or concurrently, still credits each loser exactly
once. Verified in `AuctionCompletionTests`.

Refunds never happen because someone stopped bidding — only after `WINNERS_DECLARED`.

## Rate limiting

Two layers protect the bid endpoint:

- ASP.NET Core's token-bucket limiter (10 tokens, 5/second refill) keyed on the caller
- A Redis counter per user per auction (`BidRateLimitPerSecond`, `BidRateLimitPerMinute`)

Both are configurable. Exceeding either returns `RATE_LIMITED` with HTTP 429.
