# Payments

## Payment types

```text
EntryFee · PrizePayment · Refund · StorePurchase · Deposit · Withdrawal · Other
```

Statuses: `Pending`, `Processing`, `Completed`, `Failed`, `Cancelled`, `Refunded`.

Internal wallet transfers are recorded with `Provider = "WALLET"`, so entry fees, prize payments and
store purchases appear in the payment history alongside external movements.

## Entry fee

Charged from the **Main wallet** when a user joins, inside the same transaction that creates the
participant and advances the auction's counter. The Shopping wallet can never pay an entry fee.

```text
BEGIN
  lock auction, validate registration is open and not full
  create Payment (EntryFee, Pending)
  debit Main wallet  → INSUFFICIENT_BALANCE if short
  Payment → Completed
  create participant, bump ParticipantCount
  if threshold reached: record ParticipantsCompletedAt, schedule the start
COMMIT
```

## Prize payment

Winning grants the right to a prize, not the prize itself. Each winner gets a `PrizeClaim` in
`PaymentRequired` for the prize's snapshot value, with a deadline of `PrizeClaimPaymentHours` (24 hours, as the terms of use state).

```text
Winner declared → PrizeClaim (PaymentRequired) → user pays → Paid → Claimed → Fulfilled
```

Payment comes from the **Main wallet** only — shopping credit is not cash and is refused. The claim
row is locked for the payment, and the operation is idempotency-keyed, so a retried payment charges
once. A claim already `Paid`, `Claimed` or `Fulfilled` returns its current state rather than
charging again.

## Refunds

Two kinds, deliberately kept apart:

| | Auction entry fee refund | Store order refund |
|---|---|---|
| Trigger | Losing an auction, after `WINNERS_DECLARED` | Order cancelled or refunded |
| Destination | Shopping wallet (configurable) | The wallet the order was paid from |
| Credit source | `AuctionEntryFeeRefund` | `StoreRefund` |
| Exactly-once by | `UNIQUE(AuctionId, ParticipantId)` | `order-refund:{orderId}` key |
| Tracked in | `AuctionEntryFeeRefunds` | `StoreOrders.RefundWalletTransactionId` |

A shopping-wallet purchase is always refunded as shopping credit. It never becomes withdrawable
cash — that would break the closed loop.

Entry fee refunds run one participant at a time in independent transactions, so one failure cannot
block the rest, and the refund marker is inserted **before** money moves so a concurrent duplicate
fails at the insert rather than after paying out.

## Top-ups through Paymob

Real money enters only here, into the Main wallet, by card, mada or Apple Pay on Paymob's own
checkout page (`ksa.paymob.com`). Card details never reach this server.

```text
POST /wallet/topups {amount}
  → Payment (Deposit, PAYMOB) recorded Pending; its id is the reference Paymob carries
  → Paymob intention API opens the payment          → Processing, checkout URL returned
customer pays on Paymob's page
Paymob → POST /webhooks/paymob?hmac=…      (server to server)
Paymob → GET  /payments/paymob/return?…&hmac=…   (the customer's browser)
  either one, verified with HMAC-SHA512 over Paymob's fixed field list:
    success, not pending, amount and currency equal to the recorded payment
      → credit Main wallet, keyed paymob-topup:{paymentId}   → Completed
    declined → Failed, nothing moves
GET /wallet/topups/{id}  — the app polls this until Completed or Failed
```

Both routes carry the same result and may arrive in either order or both; the credit key makes
the second a no-op. An amount that differs from the recorded payment is never credited.

Keys: `Paymob__SecretKey`, `Paymob__PublicKey`, `Paymob__HmacSecret`, `Paymob__IntegrationIds__N`
from the dashboard, set in web.config by the publish script from `local-secrets.ps1`. Without
them top-ups are switched off.

## Deposits and withdrawals

`POST /wallet/deposit` adds balance with no payment behind it, for development only. It is
refused in Production regardless of `AllowSandboxDeposits`: there it let anyone signed in add
money. `POST /wallet/withdraw` operates on the Main wallet and requires an idempotency key. Withdrawal verifies the user, the wallet, sufficient balance, writes the ledger
entry and records a `Withdrawal` payment in `Processing` for the payout provider to complete.

Shopping credit is structurally excluded from any withdrawable total.

## Webhooks

```http
POST /api/v1/webhooks/payments/{provider}
X-Signature: sha256=<hex HMAC-SHA256 of the raw body>
```

Expected payload:

```json
{
  "eventId": "evt_123",
  "transactionId": "txn_456",
  "status": "succeeded",
  "userId": "…",
  "amount": 100.00,
  "currency": "SAR"
}
```

Processing:

```text
1. read the RAW body — the signature is over exact bytes
2. verify HMAC-SHA256 in constant time      → 400 if invalid
3. parse                                     → 400 if malformed
4. INSERT PaymentWebhookEvent (raw payload, signature)
       unique violation → already delivered → 200, no money moves
5. status != succeeded → mark processed, stop
6. BEGIN
     credit the Main wallet, keyed webhook:{provider}:{eventId}
     record the Payment
     mark the event processed
   COMMIT
```

Two independent guards make redelivery safe: the unique event id, and the idempotency key on the
credit itself.

The endpoint is anonymous because a provider cannot hold a user token; authenticity comes entirely
from the signature. An unsigned request is rejected before parsing.

### Adding a real provider

`PaymentWebhookService` is the integration point. Replace the payload shape and signature scheme
with the provider's, keep the order of operations — **persist, then verify, then apply, all
idempotently** — and set `Payments:WebhookSecret` from the provider's dashboard.

## Rounding

`Money.Round` uses `MidpointRounding.AwayFromZero` to two decimals and is applied at every
boundary: prices, totals, tax, refunds and adjustments. `decimal` end to end, `numeric(18,2)` in
the database, floating point nowhere.

## Currency

Every amount is stored with its currency, and wallet movements verify the currencies match
(`CURRENCY_MISMATCH` otherwise). SAR is the configured default, not a hard-coded assumption.
