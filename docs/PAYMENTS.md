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
Unified Checkout (``https://ksa.checkout.paymob.com``). Card details never reach this server. The
integration follows Paymob's developer documentation (Intention APIs, Unified Checkout, Webhook
callbacks and HMAC, Transaction inquiry).

``````text
POST /wallet/topups {amount}
  → Payment (Deposit, PAYMOB, Pending) recorded; its id is the special_reference
  → POST ksa.paymob.com/v1/intention/  (Authorization: Token <secret key>, amount in halalas)
  → Payment.ProviderOrderId = intention_order_id (UNIQUE), status Processing
  → checkout URL returned; the customer pays on Paymob's page

Paymob → POST /webhooks/paymob?hmac=…      the "processed" callback: the confirmation
  verify HMAC-SHA512 over Paymob's 20 fields  → refused and recorded if wrong
  record the event (unique per transaction state) before applying anything
  under a FOR UPDATE lock on the payment matched by order.id:
    merchant_order_id, when present, must be this payment's id
    success, not pending/voided/refunded, amount_cents and currency exactly as recorded
      → credit Main wallet, keyed paymob-topup:{paymentId}   → Completed
    declined / voided / refunded → Failed; mismatch → Failed with AMOUNT_MISMATCH (never credited)
  a failure while applying leaves the event unprocessed and answers 500: Paymob's retry, or
  reconciliation, applies it later

Paymob → GET /payments/paymob/return?…      the "response" callback (customer's browser)
  never changes a payment (Paymob: "don't use it to update your order status"); if the payment
  is still open it asks Paymob's inquiry API, server to server, and shows the stored status

PaymobReconciliationWorker, every 3 minutes
  reapplies verified events recorded but not applied (crash, restart, database failure)
  asks POST api/ecommerce/orders/transaction_inquiry about top-ups open > 3 minutes (needs the
  API key) and applies the answer the same way; closes as Cancelled (EXPIRED) any with no
  transaction 30 minutes after its checkout expired
``````

Configuration (web.config, from ``local-secrets.ps1``): ``Paymob__SecretKey``, ``Paymob__PublicKey``,
``Paymob__HmacSecret``, ``Paymob__IntegrationIds__N``, ``Paymob__Mode`` (``test`` or ``live``; payments
whose methods Paymob marks differently are refused) and optionally ``Paymob__ApiKey`` for inquiries.
Without the four keys and a mode, top-ups are switched off.

Monitoring: ``GET /admin/payments/health`` (Wallet.Read) reports completed, failed, open and stuck
top-ups, unprocessed and rejected callbacks, unmatched confirmations, amount mismatches, refunds
after credit, failed entry-fee refunds, wallets whose balance differs from their ledger, and
whether the ledger guards are installed. ``GET /admin/payments/topups`` lists top-ups to compare
with Paymob's transaction report; ``POST /admin/payments/reconcile`` (Wallet.Adjust) runs a pass now.

## Ledger guards

``WalletTransactions`` is append-only in the database itself: a trigger refuses DELETE and any
change to a row's financial facts (only ``ExpiresAt`` may be cleared by the expiry worker).
``Payments`` cannot be deleted, nor their amount, currency, owner, type or provider changed.
``Wallets.Balance >= 0`` is a check constraint. Corrections are new, reversing entries.
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

### The generic endpoint is off

`/webhooks/payments/{provider}` credits whatever user and amount its payload names. It is not
served in Production, needs `Payments:GenericWebhookEnabled` elsewhere, and refuses any secret
shorter than 32 characters (production once ran it with the secret `not-configured`).

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
