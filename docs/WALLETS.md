# Wallets

Every user has exactly two wallets, created at registration and kept strictly separate.

| | Main wallet | Shopping wallet |
|---|---|---|
| What it is | Cash-like balance | Restricted store credit |
| Funded by | Deposits, prize refunds, admin adjustments | Losing entry fees, store refunds, admin credit, promotions |
| Spent on | Entry fees, prize payments, store purchases, withdrawals | Store purchases only |
| Withdrawable | **Yes** | **Never** |
| Transferable | No | No |
| Convertible | No | No |

`UNIQUE(UserId, WalletType)` guarantees one of each.

## No combined total

The UI never shows a single balance figure. 150 cash plus 40 credit is presented as two numbers,
because "190" would imply the whole amount can be withdrawn.

```text
┌───────────────────────────────┐   ┌───────────────────────────────┐
│ Main Wallet                   │   │ Shopping Credit               │
│ $150.00                       │   │ $40.00                        │
│ Withdrawable                  │   │ Store purchases only          │
│ [ Deposit ] [ Withdraw ]      │   │ [ Shop Now ]                  │
└───────────────────────────────┘   └───────────────────────────────┘
```

The API mirrors this: `GET /wallet` returns `{ main, shopping, currency }` and no total. The
mobile model has no combined field, and a test asserts it stays that way.

## Every balance change writes a ledger entry

A balance is never modified without a matching `WalletTransaction` in the same transaction:

```text
Id, WalletId, UserId, WalletType, TransactionType
Amount (signed)   BalanceBefore   BalanceAfter
Currency          ReferenceType   ReferenceId
CreditSource      ExpiresAt       IdempotencyKey
Description       Metadata        CreatedAt
```

`BalanceBefore` and `BalanceAfter` are both recorded, so the ledger can be replayed and reconciled
against the stored balance. A test asserts `startingBalance + Σ(ledger) == balance` after 30
concurrent movements.

Transaction types: `Deposit`, `Withdrawal`, `AuctionEntryFee`, `PrizePayment`, `ShoppingCredit`,
`StorePurchase`, `Refund`, `Adjustment`, `Expiration`.

## How a movement works

`WalletService.MoveAsync` is the only sanctioned path:

```text
1. reject a non-positive amount
2. if an idempotency key was supplied and already used → return the original result
3. ensure both wallets exist
4. BEGIN (or join the caller's transaction)
5. SELECT *, xmin FROM "Wallets" WHERE UserId = @u AND WalletType = @t FOR UPDATE
6. check currency; on a debit, check sufficient funds
7. apply the delta, write the ledger entry
8. COMMIT
```

Step 4 matters: `FOR UPDATE` only holds its lock for the life of a transaction. Called on its own,
the service opens one; called from inside checkout or a bid, it joins the caller's and its commit
is a no-op.

### Concurrency

A 30 shopping balance with two simultaneous 25 debits produces one success, one
`INSUFFICIENT_SHOPPING_CREDIT`, and a final balance of 5 — not −20, and not two successful orders.
The row lock serialises them.

### Idempotency

`UNIQUE(UserId, IdempotencyKey)` (filtered to non-null) means a repeated credit moves money once.
Ten identical refund credits leave one ledger entry and one 20 credit.

Keys used internally:

| Operation | Key |
|---|---|
| Entry fee refund | `refund:{auctionId}:{participantId}` |
| Store purchase | `order:{orderId}` |
| Store refund | `order-refund:{orderId}` |
| Prize payment | `prize:{claimId}:{clientKey}` |
| Credit expiry | `credit-expiry:{transactionId}` |
| Deposit / withdrawal | client-supplied |

## Withdrawal requires a one-time code

A withdrawal is the only operation that moves money out of the platform, so it is confirmed twice:

```text
POST /wallet/withdraw/request-otp   { amount, currency, destination }
        ↓  balance checked first, so the user is not asked to confirm something that cannot succeed
   code sent to the account's phone (or email), bound to THIS amount + currency + destination
        ↓
POST /wallet/withdraw               { amount, currency, destination, idempotencyKey, otpCode }
        ↓  code verified and consumed, then the wallet row is locked and debited
```

Because the code carries a fingerprint of the transfer, a code obtained for a small withdrawal
cannot approve a larger one or one to a different account. Both return `INVALID_OTP`; a missing
code returns `OTP_REQUIRED`; a reused one returns `OTP_ALREADY_USED`.

Turn it off with `Auction:RequireOtpForWithdrawal = false`. The Shopping wallet stays
non-withdrawable either way — the code is an extra gate on top of that rule, not a bypass.

## Restrictions in force

`WalletRules` refuses at the domain level, and the API surfaces explicit endpoints so the refusal is
documented rather than a 404 that looks like a missing feature:

```text
POST /api/v1/wallet/shopping/withdraw   → 403
POST /api/v1/wallet/shopping/transfer   → 403
POST /api/v1/wallet/shopping/convert    → 403
```

```json
{
  "code": "SHOPPING_WALLET_OPERATION_NOT_ALLOWED",
  "message": "Shopping Wallet balance can only be used for store purchases.",
  "details": []
}
```

Withdrawal draws on the Main wallet only, and `WalletRules.WithdrawableBalance` takes the main
balance alone — shopping credit is structurally excluded from any withdrawable total.

## Money representation

- C# `decimal`, PostgreSQL `numeric(18,2)` — applied to every decimal property by convention
- `Money.Round` uses `MidpointRounding.AwayFromZero`, deterministically, at every boundary
- Floating point is never used for currency
- `Amount` and `Currency` are stored together; USD is a default, not an assumption
- A `CHECK ("Balance" >= 0)` constraint is the last line of defence against a negative balance

## Credit source tracking

Every shopping-wallet credit records why it exists:

```text
AuctionEntryFeeRefund · AdminAdjustment · StoreRefund · Promotion · Bonus
```

This is what makes the closed loop accountable: issued, used and outstanding credit can be totalled
per user, per auction and platform-wide, and the admin dashboard reports outstanding credit as a
liability.

## Credit expiration

Off by default. When `ShoppingCreditExpirationEnabled` is on, credits get an `ExpiresAt` and
`ShoppingCreditExpiryWorker` sweeps hourly. Expiry is applied as an explicit `Expiration` ledger
entry for the still-unspent portion, so a balance always matches its own history — credit is never
silently removed.

## Administrative adjustments

`POST /api/v1/admin/users/{userId}/wallet-adjustment` requires the `Wallet.Adjust` permission, a
written reason and an idempotency key. Positive credits, negative debits, and the result always
produces an audit record naming the actor, the before/after balances and the reason.
