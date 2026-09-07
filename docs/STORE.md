# Store

The store closes the loop: entry fees that would otherwise be refunded as cash come back as
shopping credit, and that credit can only be spent here.

```text
User pays entry fee → loses → credit issued → user shops → credit spent → platform sells product
```

Credit is a liability, not cash, and the admin dashboard reports issued / used / outstanding
separately for exactly that reason.

## Catalog

`StoreProduct` is distinct from the auction prize catalog (`Product`): store items are *sold* and
carry stock, prizes are *awarded* and do not.

| Status | Public catalog | Purchasable |
|---|---|---|
| `Draft` | no | no |
| `Active` | yes | yes |
| `OutOfStock` | yes | no |
| `Hidden` | no | no |
| `Archived` | no | no |

Archived products stay resolvable for historical orders — a product referenced by an order is
archived, never deleted. Only a product that has never sold is physically removed.

### Payment mode

Each product declares which wallets it accepts:

```text
ShoppingWallet  ·  MainWallet  ·  Both
```

`ShoppingWallet` is the recommended default for credit-funded merchandise. Checkout enforces it:
paying with the wrong wallet returns `PAYMENT_METHOD_NOT_ACCEPTED`.

## Checkout

One transaction, with the products locked in a deterministic order:

```text
1. reject an empty cart or a missing idempotency key
2. an order already exists for this key → return it (no second purchase)
3. store enabled? payment method allowed?
4. BEGIN
5. SELECT *, xmin FROM "StoreProducts"
       WHERE "Id" = ANY(@ids) ORDER BY "Id" FOR UPDATE      ← id order prevents deadlock
6. for each line, under the lock:
       product is Active
       product accepts this wallet
       AvailableQuantity >= quantity                        ← re-read, not the browsed value
7. recalculate subtotal, shipping, tax, total  (server side, always)
8. total within the configured min/max
9. create the order with name/price/sku/image snapshots
10. decrement stock, write an InventoryTransaction per line
11. debit the wallet (locks the wallet row, writes a ledger entry)
12. mark the order Paid, record the Payment
13. COMMIT
```

Steps 5–6 are what make the last unit safe: the stock validated is the stock decremented. Two
buyers racing for one unit produce one order and one `OUT_OF_STOCK`, with stock ending at zero.

### The client never computes the total

`CalculateTotals` runs on the server and reads nothing from the request but product ids and
quantities. `POST /store/checkout/quote` returns exactly what checkout will charge:

```json
{
  "subtotal": 35.00, "discount": 0.00, "shippingFee": 0.00, "tax": 0.00, "total": 35.00,
  "currency": "USD",
  "availableBalance": 40.00, "remainingAfterPurchase": 5.00, "canAfford": true
}
```

### Idempotency

`UNIQUE(UserId, IdempotencyKey)` means a retried checkout — sequential or concurrent — returns the
original order. Eight simultaneous retries of one key produce one order and one charge.

## Order lifecycle

```text
PENDING ──► PAID ──► PROCESSING ──► SHIPPED ──► DELIVERED ──► COMPLETED
              │           │                          │              │
              └───────────┴──► CANCELLED ──► REFUNDED ◄────────────┘
```

`StoreOrderStateMachine` validates every transition. A customer can cancel while the order is
`Pending`, `Paid` or `Processing` — not once it has shipped.

### Cancellation and refunds

Cancelling restores stock (locking the product rows, writing `Cancellation` inventory transactions)
and returns the money **to the wallet it came from**:

```text
Shopping-wallet purchase  →  refunded as shopping credit
Main-wallet purchase      →  refunded to the Main wallet
```

A shopping-wallet purchase never becomes withdrawable cash. The refund carries
`CreditSource = StoreRefund` and is keyed `order-refund:{orderId}`, so a repeated refund moves
money once.

## Inventory

`StoreProduct` tracks `StockQuantity`, `ReservedQuantity`, `SoldQuantity` and `LowStockThreshold`;
`AvailableQuantity = StockQuantity − ReservedQuantity`.

Every change writes an `InventoryTransaction`:

```text
Initial · Purchase · Adjustment · Restock · Return · Cancellation
```

Each records `BeforeQuantity`, `AfterQuantity`, the reference (order or admin action), a reason and
the acting admin. Manual adjustments require a written reason, take a row lock, and produce an
audit record. Stock cannot go negative — the service refuses and a `CHECK` constraint backs it up.

Crossing `LowStockThreshold` is logged as a structured warning and surfaced through the admin
inventory filter.

## Store settings

A single configurable row:

```text
StoreEnabled                       ShoppingWalletEnabled
AllowShoppingWalletPurchases       AllowMainWalletPurchases
DefaultCurrency
TaxEnabled / TaxPercentage
ShippingEnabled / DefaultShippingFee
MinimumOrderAmount / MaximumOrderAmount
ShoppingCreditExpirationEnabled / ShoppingCreditExpirationDays
LowStockThreshold                  RefundPolicy
```

Changes are audited with before/after values.

## Admin

The dashboard's Store section covers Overview, Products, Categories, Inventory, Orders and
Settings. Overview reports:

```text
Products · Active · Out of stock · Low stock
Orders · Pending · Completed · Store revenue
Shopping credit issued · used · outstanding
```

Product creation, edits, status changes, inventory adjustments, order transitions and refunds all
require the matching `Store.*` or `Wallet.*` permission and all write audit records.

## Mobile

```text
StoreScreen           credit balance, categories, credit-eligible / featured / new / popular
StoreProductsScreen   search and category filtering
ProductDetailScreen   images, description, stock, accepted wallets
CartScreen            local cart, server quote, wallet choice, checkout
OrdersScreen          order history and cancellation
```

The credit balance leads the store, because spending it is why most users are there. Products that
accept credit are marked with a distinct badge.
