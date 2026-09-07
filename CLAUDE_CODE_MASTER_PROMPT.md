# Production-Grade Real-Time Auction Platform

## Complete Master Specification — ASP.NET Core + PostgreSQL + SignalR + Redis + Flutter

> **Purpose:** This document is the authoritative product, business, architecture, database, API, security, real-time, wallet, auction, store, and implementation specification for the platform.
>
> **Implementation target:** Production-grade, scalable, secure, transactional, real-time auction platform.
>
> **Backend:** ASP.NET Core 10 Web API
> **Database:** PostgreSQL
> **Real-time:** ASP.NET Core SignalR
> **Cache / distributed coordination:** Redis
> **Authentication:** JWT + Refresh Token Rotation
> **Mobile:** Flutter
> **Notifications:** Firebase Cloud Messaging
> **ORM:** Entity Framework Core
> **Logging:** Serilog
> **Observability:** OpenTelemetry
> **Containerization:** Docker

---

# 1. IMPORTANT INSTRUCTION TO CLAUDE CODE

You are implementing an existing production-grade real-time auction platform.

Before changing anything:

1. Inspect the existing repository.
2. Inspect the current solution structure.
3. Inspect existing database models and migrations.
4. Inspect existing authentication.
5. Inspect existing API endpoints.
6. Inspect existing Flutter architecture.
7. Inspect current environment configuration.
8. Do NOT blindly overwrite working code.
9. Reuse existing working functionality where appropriate.
10. Identify conflicts between the current implementation and this specification.
11. Implement incrementally.
12. Build and test after every major phase.
13. Never move business-critical logic into Flutter.
14. Backend is the authoritative source of truth.

After every implementation phase, report:

```text
Implemented:
Changed files:
Database changes:
API changes:
Flutter changes:
Tests:
Potential issues:
Next phase:
```

---

# 2. PRODUCT OVERVIEW

The platform is a real-time auction system where users pay an entry fee to participate.

Each auction contains exactly four products/prizes:

```text
Prize #1 → Main Product → 1st place
Prize #2 → Product 2    → 2nd place
Prize #3 → Product 3    → 3rd place
Prize #4 → Product 4    → 4th place
```

Only the main product participates in the price competition.

The other three products are prizes awarded to the next ranked participants.

---

# 3. CORE AUCTION CONCEPT

The auction is based on reaching a target price.

Example:

```text
Original Product Price = $5,000

Target Percentage = 50%

Target Price = $2,500

Starting Price = $1,000

Bid Increment = $20
```

Price progression:

```text
$1,000
$1,020
$1,040
...
$2,460
$2,480
$2,500
```

When a participant successfully reaches `$2,500`, the auction ends immediately.

---

# 4. IMPORTANT BID RULE

The bid increment is NOT money charged to the participant.

If:

```text
Current Price = $2,480
Bid Increment = $20
```

the next successful bid makes:

```text
Current Price = $2,500
```

The participant does NOT pay another $20 merely for placing the bid.

The entry fee is a separate payment.

---

# 5. AUCTION DEFAULT SETTINGS

Default configuration:

```text
Entry Fee: $20
Required Participants: 400
Start Delay After Required Participants: configurable
Maximum Duration: 3 minutes
Target Percentage: 50%
Final Phase Percentage: 40%
Bid Increment: configurable
```

All values must be configurable by the administrator.

---

# 6. TARGET PRICE

Target price:

```text
TargetPrice = OriginalPrice × TargetPercentage / 100
```

Example:

```text
OriginalPrice = $5,000
TargetPercentage = 50%

TargetPrice = $2,500
```

Persist both:

```text
TargetPercentage
TargetPrice
```

Do not recalculate historical auction values from current product prices.

---

# 7. FINAL PHASE

The final phase starts when:

```text
CurrentPrice >= FinalPhasePrice
```

where:

```text
FinalPhasePrice =
OriginalPrice × FinalPhasePercentage / 100
```

Example:

```text
Original Price = $5,000
Final Phase = 40%

Final Phase Price = $2,000
Target = $2,500
```

When price reaches `$2,000`:

```text
FINAL_PHASE_STARTED
```

The UI must become more urgent.

Final phase requirements:

* Timer permanently visible.
* Red/orange visual treatment.
* Strong bid button.
* Live price animation.
* Live ranking.
* Participant rank updates.
* Target progress.
* Real-time bid notifications.
* Clear remaining time.

---

# 8. PARTICIPANT REQUIREMENT

Each auction has:

```text
RequiredParticipants
```

Default:

```text
400
```

The auction does NOT start merely because it was created.

The auction becomes ready only when:

```text
ParticipantCount >= RequiredParticipants
```

Example:

```text
399 / 400
```

Auction remains waiting.

When participant #400 joins:

```text
400 / 400
```

the system records:

```text
ParticipantsCompletedAt
```

and calculates:

```text
ScheduledStartAt =
ParticipantsCompletedAt +
StartDelayAfterParticipantsSeconds
```

---

# 9. START DELAY

Example:

```text
Required Participants = 400

Start Delay = 2 hours
```

When participant #400 joins:

```text
ParticipantsCompletedAt = 14:00 UTC

ScheduledStartAt = 16:00 UTC
```

The auction is now:

```text
READY / WAITING_FOR_START
```

Users receive a notification.

---

# 10. ADMIN START CONTROL

The administrator can:

```text
START NOW
```

or:

```text
EDIT START TIME
```

The administrator can reduce the scheduled time or start immediately.

Every change must be audited.

Events:

```text
REQUIRED_PARTICIPANTS_REACHED
AUCTION_START_SCHEDULED
AUCTION_START_TIME_CHANGED
AUCTION_STARTED_MANUALLY
AUCTION_STARTED
```

All participants receive appropriate notifications.

---

# 11. AUCTION STATE MACHINE

Recommended states:

```text
DRAFT
SCHEDULED
REGISTRATION_OPEN
WAITING_FOR_PARTICIPANTS
READY
WAITING_FOR_START
LIVE
FINAL_PHASE
FINISHED
WINNERS_DECLARED
PRIZE_CLAIM
COMPLETED
PAUSED
CANCELLED
EXPIRED
```

Transitions must be validated by the backend.

No arbitrary state changes from the client are allowed.

---

# 12. AUCTION END CONDITIONS

Primary end condition:

```text
CurrentPrice >= TargetPrice
```

The first valid bid that reaches the target ends the auction immediately.

The backend must:

1. Accept the bid atomically.
2. Increase the price.
3. Record the participant's highest price.
4. Record server timestamp.
5. Mark target reached.
6. Finish the auction.
7. Prevent further bids.
8. Calculate rankings.
9. Declare winners.
10. Start prize/refund processing.
11. Broadcast final state.

---

# 13. AUCTION MAXIMUM DURATION

Default:

```text
3 minutes
```

The duration begins when:

```text
AUCTION_STARTED
```

not when the auction is created and not when registration opens.

If the target is reached before 3 minutes:

```text
AUCTION_FINISHED
```

immediately.

If duration expires before target is reached, implement a configurable expiration policy.

Supported policies:

```text
END_AND_DISTRIBUTE
EXTEND
CANCEL
RESTART
```

Recommended default:

```text
END_AND_DISTRIBUTE
```

This policy must be configurable at auction level.

---

# 14. RANKING SYSTEM

Ranking is based on:

### Primary rule

Highest price reached by participant.

### Tie-break

Earliest server timestamp at which that participant reached that price.

Example:

```text
Participant A → $2,400 at 12:01:20.100
Participant B → $2,400 at 12:01:21.200
```

A ranks higher.

The device timestamp must never be trusted.

---

# 15. TOP FOUR WINNERS

Exactly four winners:

```text
Rank #1 → Prize #1
Rank #2 → Prize #2
Rank #3 → Prize #3
Rank #4 → Prize #4
```

The final participant reaching the target may become Rank #1 depending on their highest reached price and ranking rules.

All other participants are ranked by their highest reached price and server timestamp.

---

# 16. AUCTION PRIZES

Every published auction MUST contain exactly four prizes.

```text
Rank 1
Rank 2
Rank 3
Rank 4
```

The main product:

```text
AuctionPrize Rank 1
```

must also be the main auction product.

---

# 17. AUCTION CREATION VALIDATION

Before publishing:

```text
StartingPrice > 0
StartingPrice < TargetPrice
TargetPrice < OriginalPrice
FinalPhasePrice < TargetPrice
BidIncrement > 0
EntryFee >= 0
RequiredParticipants >= 1
Duration > 0
Exactly 4 prizes
Unique prize rank 1–4
```

An auction cannot be published if these conditions are invalid.

---

# 18. AUCTION DATABASE ENTITY

## Auction

```text
Id
Title
Description

MainProductId

OriginalPrice
StartingPrice
CurrentPrice

TargetPercentage
TargetPrice

FinalPhasePercentage
FinalPhasePrice

BidIncrement

EntryFee
Currency

RequiredParticipants

StartDelayAfterParticipantsSeconds

DurationSeconds

ParticipantsCompletedAt
ScheduledStartAt
ActualStartedAt
FinalPhaseStartedAt
FinishedAt

Status
ExpirationPolicy

CreatedAt
UpdatedAt
```

All timestamps are UTC.

---

# 19. AUCTION PRIZE ENTITY

```text
AuctionPrize

Id
AuctionId
ProductId
Rank
OriginalPrice
CreatedAt
```

Constraints:

```text
UNIQUE(AuctionId, Rank)
```

Rank must be:

```text
1
2
3
4
```

---

# 20. PARTICIPANT ENTITY

```text
AuctionParticipant

Id
AuctionId
UserId

EntryFeePaymentId

JoinedAt

CurrentPrice
HighestPrice

CurrentRank
BidCount
LastBidAt

Status
```

Constraint:

```text
UNIQUE(AuctionId, UserId)
```

A user cannot join the same auction twice.

---

# 21. BID ENTITY

```text
Bid

Id
AuctionId
ParticipantId

SequenceNumber

PreviousPrice
NewPrice

IdempotencyKey

CreatedAt
ServerTimestamp
```

Constraints:

```text
UNIQUE(AuctionId, SequenceNumber)
UNIQUE(AuctionId, ParticipantId, IdempotencyKey)
```

---

# 22. BID ENGINE

The bid engine is one of the most critical components.

It must be:

* Transactional
* Atomic
* Concurrent-safe
* Idempotent
* Multi-instance safe
* Database authoritative

Never use an in-memory lock as the primary concurrency mechanism.

---

# 23. BID TRANSACTION

A valid bid must execute inside a database transaction.

Conceptually:

```text
BEGIN TRANSACTION

Lock auction

Verify auction is LIVE / FINAL_PHASE

Verify auction has not expired

Verify target has not been reached

Read current price

Calculate:

newPrice =
currentPrice + bidIncrement

Create Bid

Update Auction.CurrentPrice

Update Participant.HighestPrice

Update Participant.BidCount

Update Participant.LastBidAt

Assign SequenceNumber

If newPrice >= target:
    mark auction finished

COMMIT
```

Only after COMMIT may the successful bid be broadcast through SignalR.

---

# 24. DUPLICATE BID REQUESTS

Every bid request must contain:

```text
idempotencyKey
```

If the same request is retried:

```text
same IdempotencyKey
```

the backend must return the existing result rather than create another bid.

---

# 25. CONCURRENT BIDS

Example:

```text
Current Price = $2,480
Target = $2,500
Increment = $20
```

100 simultaneous bid requests arrive.

Expected result:

```text
Exactly one bid reaches $2,500.
Auction finishes.
No bids after closing succeed.
No duplicate price changes occur.
```

This must be covered by concurrency integration tests.

---

# 26. BACKEND AUTHORITY

The Flutter application must NEVER determine:

```text
Winner
Rank
Current Price
Auction End
Server Timestamp
Target Reached
Bid Sequence
Refund Amount
Wallet Balance
```

The backend determines all of these.

Flutter only displays authoritative server state.

---

# 27. REAL-TIME ARCHITECTURE

Use:

```text
ASP.NET Core SignalR
```

Hub:

```text
/hubs/auction
```

Group:

```text
auction:{auctionId}
```

SignalR is a delivery mechanism, not the source of truth.

PostgreSQL remains authoritative.

---

# 28. SIGNALR EVENTS

Support:

```text
AuctionParticipantCountChanged
AuctionReady
AuctionStartScheduled
AuctionStartTimeChanged
AuctionStarted

BidPlaced
AuctionPriceChanged
ParticipantRankChanged
LeaderboardUpdated

FinalPhaseStarted

AuctionFinished
WinnersDeclared

PrizeClaimAvailable
ShoppingCreditAdded

AuctionCancelled
AuctionPaused
AuctionResumed
```

---

# 29. SIGNALR RECONNECT

When Flutter reconnects:

```text
Authenticate
↓
Join auction group
↓
GET auction snapshot
↓
Synchronize:
    CurrentPrice
    ParticipantCount
    CurrentRank
    Leaderboard
    Status
    Timer
↓
Continue SignalR updates
```

Never trust the client to reconstruct missing events.

---

# 30. AUCTION SNAPSHOT

Example:

```json
{
  "id": "auction-id",
  "status": "FINAL_PHASE",
  "currentPrice": 2380.00,
  "targetPrice": 2500.00,
  "finalPhasePrice": 2000.00,
  "participantCount": 400,
  "requiredParticipants": 400,
  "remainingSeconds": 72,
  "myRank": 3,
  "myHighestPrice": 2340.00
}
```

---

# 31. ENTRY FEE

Default:

```text
$20
```

The entry fee is separate from bid increment.

Joining requires successful payment of the entry fee.

Default payment source:

```text
MAIN_WALLET
```

---

# 32. TWO-WALLET SYSTEM

Each user has two completely separate wallets:

```text
MAIN WALLET
SHOPPING WALLET
```

---

# 33. MAIN WALLET

Main Wallet is the user's cash-like balance.

Supported:

```text
Deposit
Withdraw
Auction Entry Fee
Prize Payment
```

The balance may be withdrawn according to platform rules.

Example:

```text
Main Wallet:
$150

Withdrawable:
Yes
```

---

# 34. SHOPPING WALLET

Shopping Wallet is restricted store credit.

It may be used ONLY to purchase products from the platform's store.

It cannot:

```text
Be withdrawn
Be transferred
Be converted into Main Wallet
Be cashed out
```

Example:

```text
Shopping Wallet:
$40

Withdrawable:
No

Usage:
Store purchases only
```

---

# 35. LOSING PARTICIPANT REFUND

When a participant loses an auction:

```text
Entry Fee
        ↓
NOT Main Wallet
        ↓
Shopping Wallet
```

Example:

```text
Entry Fee = $20

Participant loses

Shopping Wallet:
+$20
```

This is a store credit refund.

---

# 36. WINNER ENTRY FEE

Default:

```text
Winner entry fee is NOT refunded.
```

Losers receive:

```text
Entry Fee → Shopping Wallet
```

Make this configurable:

```text
RefundLosingParticipantsEntryFee = true
RefundWinnerEntryFee = false
RefundDestinationWallet = SHOPPING
```

---

# 37. REFUND TIMING

Refunds occur only after:

```text
AUCTION_FINISHED
↓
WINNERS_DECLARED
↓
Losers determined
↓
Refund processing
```

Never refund simply because a participant stopped bidding.

---

# 38. REFUND TRACKING

Create:

```text
AuctionEntryFeeRefund
```

Fields:

```text
Id
AuctionId
ParticipantId
UserId

OriginalEntryFee
RefundAmount
Currency

Status

WalletTransactionId

CreatedAt
ProcessedAt
```

Statuses:

```text
PENDING
PROCESSING
COMPLETED
FAILED
```

Constraint:

```text
UNIQUE(AuctionId, ParticipantId)
```

This prevents double refunds.

---

# 39. WALLET ENTITY

Recommended:

```text
Wallet

Id
UserId
WalletType
Balance
Currency
CreatedAt
UpdatedAt
```

Wallet types:

```text
MAIN
SHOPPING
```

Constraint:

```text
UNIQUE(UserId, WalletType)
```

---

# 40. WALLET TRANSACTION LEDGER

Every balance change must create a transaction.

```text
WalletTransaction

Id
UserId
WalletType
TransactionType

Amount
Currency

BalanceBefore
BalanceAfter

ReferenceType
ReferenceId

Description
Metadata

CreatedAt
```

Transaction types:

```text
DEPOSIT
WITHDRAWAL

AUCTION_ENTRY_FEE
PRIZE_PAYMENT

SHOPPING_CREDIT
STORE_PURCHASE

REFUND
ADJUSTMENT
```

Never modify a wallet balance without an associated ledger entry.

---

# 41. MONEY TYPES

Never use:

```text
float
double
```

for money.

Use:

```text
C# decimal
PostgreSQL numeric(18,2)
```

---

# 42. WALLET SECURITY

The client cannot directly modify:

```text
Balance
Transaction
Refund
Credit
Withdrawal
```

All operations must go through authorized backend commands.

---

# 43. SHOPPING STORE

The platform must contain a dedicated store.

Users can spend Shopping Wallet credit on store products.

The store is managed by administrators.

---

# 44. STORE PRODUCT

Entity:

```text
StoreProduct

Id
Name
Slug
Description

CategoryId

SKU

Price
Currency

StockQuantity

Status

Images

CreatedAt
UpdatedAt
```

Statuses:

```text
DRAFT
ACTIVE
OUT_OF_STOCK
HIDDEN
ARCHIVED
```

---

# 45. PRODUCT CATEGORIES

Entity:

```text
StoreCategory

Id
Name
Slug
Description
Image
DisplayOrder
Status
CreatedAt
UpdatedAt
```

Categories are managed by administrators.

---

# 46. STORE PRODUCT PAYMENT MODE

Products can support:

```text
SHOPPING_WALLET
MAIN_WALLET
BOTH
```

Recommended default for refunded-credit products:

```text
SHOPPING_WALLET
```

---

# 47. STORE PURCHASE FLOW

User selects product.

Backend:

```text
Verify product exists
↓
Verify ACTIVE
↓
Verify stock
↓
Verify Shopping Wallet balance
↓
Lock wallet
↓
Lock/revalidate inventory
↓
Create order
↓
Deduct Shopping Wallet
↓
Create wallet transaction
↓
Decrease stock
↓
Commit
```

Everything must be atomic.

---

# 48. STORE ORDER ENTITY

```text
StoreOrder

Id
UserId
OrderNumber

Subtotal
Discount
Total
Currency

PaymentMethod

Status

ShippingAddressId

CreatedAt
PaidAt
CompletedAt
CancelledAt
```

Payment methods:

```text
SHOPPING_WALLET
MAIN_WALLET
```

---

# 49. STORE ORDER ITEM

```text
StoreOrderItem

Id
OrderId
ProductId

ProductNameSnapshot

UnitPrice
Quantity
TotalPrice
```

Historical product name and price must be stored as snapshots.

---

# 50. STORE ORDER STATUS

```text
PENDING
PAID
PROCESSING
SHIPPED
DELIVERED
COMPLETED
CANCELLED
REFUNDED
```

---

# 51. STORE REFUNDS

Store refunds must be carefully separated from auction entry-fee refunds.

If a store order is cancelled:

```text
Shopping Wallet purchase
        ↓
Store refund
        ↓
Shopping Wallet
```

unless administrator configuration explicitly permits another method.

A Shopping Wallet purchase should not automatically become Main Wallet cash.

---

# 52. SHOPPING WALLET RESTRICTIONS

The backend must reject:

```text
POST /wallet/shopping/withdraw
POST /wallet/shopping/transfer
POST /wallet/shopping/convert
```

Error:

```json
{
  "code": "SHOPPING_WALLET_OPERATION_NOT_ALLOWED",
  "message": "Shopping Wallet balance can only be used for store purchases.",
  "details": []
}
```

---

# 53. USER WALLET UI

The mobile application must clearly separate the balances.

```text
┌───────────────────────────────┐
│ Main Wallet                   │
│ $150.00                       │
│ Withdrawable                  │
│                               │
│ [ Deposit ] [ Withdraw ]      │
└───────────────────────────────┘

┌───────────────────────────────┐
│ Shopping Credit               │
│ $40.00                        │
│ Store purchases only          │
│                               │
│ [ Shop Now ]                  │
└───────────────────────────────┘
```

Never display:

```text
Total Balance: $190
```

as the primary balance because this can incorrectly imply that the whole amount is withdrawable.

---

# 54. SHOPPING STORE USER EXPERIENCE

Flutter screens:

```text
StoreHomeScreen
StoreCategoryScreen
StoreProductDetailsScreen
CartScreen
CheckoutScreen
OrderConfirmationScreen
MyOrdersScreen
OrderDetailsScreen
ShoppingWalletScreen
```

---

# 55. STORE HOME

Display:

```text
Featured Products
Categories
New Products
Popular Products
Products Available With Shopping Credit
```

Example:

```text
SHOP

[ Electronics ]
[ Accessories ]
[ Home ]

Featured

┌───────────────┐
│ Product Image │
│ Headphones    │
│ $35           │
│               │
│ Buy with      │
│ Shopping      │
│ Credit        │
└───────────────┘
```

---

# 56. PRODUCT DETAILS

Show:

```text
Product Images
Name
Description
Price
Availability
Stock
Payment methods
Shipping information
```

Clearly display:

```text
Shopping Credit Accepted
```

when applicable.

---

# 57. ADMIN STORE MANAGEMENT

The Admin Dashboard MUST contain a dedicated:

```text
Store Management
```

section.

Navigation:

```text
Admin Dashboard
│
├── Dashboard
├── Auctions
├── Products
├── Participants
├── Winners
├── Payments
├── Wallets
│
└── Store
    ├── Overview
    ├── Products
    ├── Categories
    ├── Inventory
    ├── Orders
    ├── Customers
    ├── Shopping Credits
    └── Store Settings
```

---

# 58. ADMIN STORE OVERVIEW SCREEN

Display:

```text
Total Products
Active Products
Out of Stock
Total Orders
Pending Orders
Completed Orders
Store Revenue
Shopping Credit Used
Shopping Credit Outstanding
```

Example:

```text
┌──────────────────────────────────────────┐
│ STORE OVERVIEW                           │
├──────────────────────────────────────────┤
│ Products             128                  │
│ Active               112                  │
│ Out of Stock          16                  │
│                                          │
│ Orders               1,245                │
│ Pending                32                 │
│ Completed            1,180               │
│                                          │
│ Store Revenue       $48,500               │
│ Credit Used         $21,340               │
└──────────────────────────────────────────┘
```

---

# 59. ADMIN PRODUCT MANAGEMENT SCREEN

Admin can:

```text
Create Product
Edit Product
Delete/Archive Product
Activate Product
Hide Product
Manage Images
Manage Price
Manage Stock
Manage SKU
Assign Category
Configure Payment Method
```

Product form:

```text
Product Name
Slug
Description
Category
SKU
Price
Currency
Stock Quantity
Images
Status
Payment Method
Shipping Configuration
```

---

# 60. ADMIN ADD PRODUCT SCREEN

Recommended wizard:

```text
Step 1
Basic Information

Step 2
Images

Step 3
Pricing

Step 4
Inventory

Step 5
Payment Options

Step 6
Shipping

Step 7
Review & Publish
```

---

# 61. ADMIN PRODUCT LIST

Columns:

```text
Image
Product
SKU
Category
Price
Stock
Payment Method
Status
Created
Actions
```

Actions:

```text
Edit
Duplicate
Hide
Archive
Adjust Stock
View Orders
```

---

# 62. INVENTORY MANAGEMENT

Admin must be able to see:

```text
Current Stock
Reserved Stock
Available Stock
Sold Quantity
```

Stock adjustments must be audited.

Example:

```text
Previous Stock: 50
Adjustment: +20
New Stock: 70

Reason:
New inventory shipment
```

---

# 63. INVENTORY TRANSACTION

Create:

```text
InventoryTransaction

Id
ProductId
Type
Quantity
BeforeQuantity
AfterQuantity
ReferenceType
ReferenceId
Reason
AdminUserId
CreatedAt
```

Types:

```text
INITIAL
PURCHASE
ADJUSTMENT
RESTOCK
RETURN
CANCELLATION
```

---

# 64. ADMIN STORE ORDERS SCREEN

Admin can filter by:

```text
Order Number
Customer
Status
Date
Payment Method
Product
```

Order statuses:

```text
PENDING
PAID
PROCESSING
SHIPPED
DELIVERED
COMPLETED
CANCELLED
REFUNDED
```

---

# 65. ADMIN ORDER DETAILS

Show:

```text
Order Number
Customer
Products
Quantities
Prices
Subtotal
Discount
Total
Payment Method
Payment Status
Shipping Address
Order Timeline
```

Actions:

```text
Mark Processing
Mark Shipped
Mark Delivered
Mark Completed
Cancel
Refund
```

All administrative actions must be audited.

---

# 66. ADMIN SHOPPING CREDIT SCREEN

Admin can view:

```text
Total Shopping Credit Outstanding
Credits Issued
Credits Used
Credits Remaining
Credits By Auction
Credits By User
```

Example:

```text
Shopping Credits

Issued:       $85,000
Used:         $42,500
Outstanding:  $42,500
```

---

# 67. ADMIN USER SHOPPING CREDIT

Admin can inspect:

```text
User
Main Wallet
Shopping Wallet
Total Auction Refunds
Shopping Purchases
Remaining Credit
```

Manual credit adjustments may be permitted only to authorized admin roles.

Every adjustment requires:

```text
Amount
Reason
Admin User
Timestamp
Audit Record
```

---

# 68. ADMIN STORE SETTINGS

Settings:

```text
Store Enabled
Shopping Wallet Enabled
Allow Shopping Wallet Purchases
Allow Main Wallet Purchases
Default Currency
Tax Configuration
Shipping Enabled
Minimum Order
Maximum Order
Refund Policy
```

---

# 69. ADMIN AUCTION DASHBOARD

Dashboard should display:

```text
Active Auctions
Upcoming Auctions
Completed Auctions
Participants
Revenue
Pending Payments
Prize Claims
Shopping Credits Issued
Store Sales
```

---

# 70. AUCTION ADMIN READY SCREEN

When participant count reaches required number:

```text
AUCTION READY

400 / 400

Participants completed:
07 Sep 2026 14:00 UTC

Scheduled Start:
07 Sep 2026 16:00 UTC

[ START NOW ]
[ EDIT TIME ]
```

---

# 71. ADMIN AUCTION CREATION WIZARD

Steps:

```text
1. Basic Information
2. Main Product
3. Prize #2
4. Prize #3
5. Prize #4
6. Auction Settings
7. Participant Settings
8. Start Delay
9. Expiration Policy
10. Review
11. Publish
```

---

# 72. ADMIN AUCTION SETTINGS

Fields:

```text
Title
Description

Main Product

Original Price
Starting Price

Target Percentage
Target Price

Final Phase Percentage
Final Phase Price

Bid Increment

Entry Fee

Required Participants

Start Delay

Duration

Expiration Policy
```

---

# 73. ADMIN AUCTION PARTICIPANTS

Admin can see:

```text
Participant
Joined At
Entry Fee
Highest Price
Bid Count
Current Rank
Status
```

Never allow admin UI to manually change bid history.

---

# 74. WINNERS

Winner entity:

```text
Winner

Id
AuctionId
ParticipantId
Rank
PrizeId
HighestPrice
RankedAt
PrizeClaimId
```

---

# 75. PRIZE CLAIM

Entity:

```text
PrizeClaim

Id
WinnerId
UserId
PrizeId

Status

PaymentAmount
Currency

CreatedAt
PaymentRequiredAt
PaidAt
ClaimedAt
FulfilledAt
CancelledAt
ExpiredAt
```

Statuses:

```text
PENDING
PAYMENT_REQUIRED
PAYMENT_PROCESSING
PAID
CLAIMED
FULFILLED
CANCELLED
EXPIRED
```

---

# 76. WINNER PRIZE PAYMENT

Winning a prize does NOT mean the participant has already paid the product value.

After winning:

```text
Winner Declared
↓
Prize Claim
↓
Payment Required
↓
User Pays
↓
Payment Confirmed
↓
Prize Fulfillment
```

---

# 77. PAYMENT ENTITY

```text
Payment

Id
UserId
AuctionId
PaymentType
Amount
Currency
Provider
ProviderTransactionId
Status
CreatedAt
CompletedAt
```

Payment types:

```text
ENTRY_FEE
PRIZE_PAYMENT
REFUND
STORE_PURCHASE
OTHER
```

---

# 78. STORE PAYMENT

A Shopping Wallet purchase must create:

```text
StoreOrder
WalletTransaction
```

and maintain a complete audit trail.

Example:

```text
Shopping Wallet = $50

Purchase = $30

Wallet:
$50 → $20

Transaction:
STORE_PURCHASE -$30
```

---

# 79. NOTIFICATIONS

Use:

```text
Firebase Cloud Messaging
```

Notifications include:

```text
Required Participants Reached
Auction Start Scheduled
Auction Start Time Changed
Auction Started
Final Phase Started
Auction Finished
You Won
You Lost
Shopping Credit Added
Prize Claim Available
Prize Payment Reminder
Store Order Created
Order Shipped
Order Delivered
```

---

# 80. NOTIFICATION ENTITY

```text
Notification

Id
UserId
Type
Title
Body
Data
ReadAt
CreatedAt
```

---

# 81. USER RANK UI

During auction:

```text
Your Rank: #3

🎉 أنت الآن ضمن الفائزين!
```

If user falls from #4:

```text
⚠️ فقدت المركز الرابع!
```

Rank must be server-generated.

---

# 82. PRE-THRESHOLD UI

Before required participants:

```text
372 / 400 Participants

28 participants remaining

Entry Fee:
$20

[ JOIN AUCTION ]
```

---

# 83. POST-THRESHOLD UI

After reaching required participants:

```text
400 / 400

✓ العدد اكتمل

Auction starts:
01:42:35

You will receive a notification when the auction starts.
```

---

# 84. LIVE AUCTION UI

Show:

```text
Main Product
Original Price
Current Auction Price
Target Price
Progress
Participant Count
Your Highest Price
Your Rank
Top 4
Bid Button
```

Bid button:

```text
[ +$20 BID ]
```

The amount shown is the bid increment, not a fee.

---

# 85. FINAL PHASE UI

Example:

```text
🔥 FINAL PHASE

Current:
$2,340

Target:
$2,500

Remaining:
00:38

Your Rank:
#4

[ +$20 BID ]
```

Use animations carefully without affecting performance.

---

# 86. USER STORE NAVIGATION

Main mobile navigation should include:

```text
Home
Auctions
Store
Wallet
Profile
```

Store must clearly distinguish:

```text
Main Wallet
Shopping Credit
```

---

# 87. USER STORE CHECKOUT

Checkout must show:

```text
Order
Products
Quantity
Subtotal
Shipping
Discount
Total

Payment:
Shopping Wallet

Available Shopping Credit:
$40

Required:
$35

Remaining:
$5
```

---

# 88. API ARCHITECTURE

Base:

```text
/api/v1
```

---

# 89. AUTH API

```text
POST /api/v1/auth/register
POST /api/v1/auth/login
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
POST /api/v1/auth/verify-otp
POST /api/v1/auth/forgot-password
POST /api/v1/auth/reset-password
```

---

# 90. AUCTION API

```text
GET    /api/v1/auctions
GET    /api/v1/auctions/{id}

POST   /api/v1/admin/auctions
PUT    /api/v1/admin/auctions/{id}
DELETE /api/v1/admin/auctions/{id}

POST   /api/v1/admin/auctions/{id}/publish
POST   /api/v1/admin/auctions/{id}/start
POST   /api/v1/admin/auctions/{id}/pause
POST   /api/v1/admin/auctions/{id}/resume
POST   /api/v1/admin/auctions/{id}/cancel

PATCH  /api/v1/admin/auctions/{id}/schedule
```

---

# 91. PARTICIPATION API

```text
POST /api/v1/auctions/{id}/join
GET  /api/v1/auctions/{id}/participants
GET  /api/v1/auctions/{id}/my-participation
```

---

# 92. BID API

```text
POST /api/v1/auctions/{id}/bids
```

Request:

```json
{
  "idempotencyKey": "unique-client-generated-id"
}
```

The server determines:

```text
Previous Price
New Price
Sequence Number
Timestamp
```

---

# 93. WINNER API

```text
GET /api/v1/auctions/{id}/winners
GET /api/v1/me/wins
GET /api/v1/me/prize-claims
GET /api/v1/me/prize-claims/{id}
POST /api/v1/prize-claims/{id}/pay
POST /api/v1/prize-claims/{id}/claim
```

---

# 94. WALLET API

```text
GET  /api/v1/wallet
GET  /api/v1/wallet/transactions

POST /api/v1/wallet/deposit
POST /api/v1/wallet/withdraw
```

Shopping wallet:

```text
GET /api/v1/wallet/shopping
GET /api/v1/wallet/shopping/transactions
```

Do NOT expose withdrawal for Shopping Wallet.

---

# 95. STORE USER API

```text
GET /api/v1/store/products
GET /api/v1/store/products/{id}
GET /api/v1/store/categories
GET /api/v1/store/categories/{id}

POST /api/v1/store/orders
GET  /api/v1/store/orders
GET  /api/v1/store/orders/{id}
POST /api/v1/store/orders/{id}/cancel
```

---

# 96. ADMIN STORE API

```text
GET    /api/v1/admin/store/products
POST   /api/v1/admin/store/products
GET    /api/v1/admin/store/products/{id}
PUT    /api/v1/admin/store/products/{id}
DELETE /api/v1/admin/store/products/{id}

POST   /api/v1/admin/store/products/{id}/activate
POST   /api/v1/admin/store/products/{id}/hide
POST   /api/v1/admin/store/products/{id}/archive

GET    /api/v1/admin/store/categories
POST   /api/v1/admin/store/categories
PUT    /api/v1/admin/store/categories/{id}
DELETE /api/v1/admin/store/categories/{id}

GET    /api/v1/admin/store/inventory
POST   /api/v1/admin/store/inventory/{productId}/adjust

GET    /api/v1/admin/store/orders
GET    /api/v1/admin/store/orders/{id}
POST   /api/v1/admin/store/orders/{id}/process
POST   /api/v1/admin/store/orders/{id}/ship
POST   /api/v1/admin/store/orders/{id}/deliver
POST   /api/v1/admin/store/orders/{id}/cancel
POST   /api/v1/admin/store/orders/{id}/refund

GET    /api/v1/admin/store/statistics
```

---

# 97. ADMIN WALLET API

```text
GET /api/v1/admin/wallets
GET /api/v1/admin/wallets/{userId}

GET /api/v1/admin/shopping-credits
POST /api/v1/admin/users/{userId}/shopping-credit
```

Manual credits require permission and audit logging.

---

# 98. API ERROR FORMAT

Standard:

```json
{
  "code": "AUCTION_NOT_ACTIVE",
  "message": "The auction is not currently active.",
  "details": []
}
```

Standard error codes:

```text
AUCTION_NOT_FOUND
AUCTION_NOT_ACTIVE
AUCTION_FINISHED
AUCTION_NOT_READY

PARTICIPANT_NOT_FOUND
ALREADY_JOINED
ENTRY_PAYMENT_REQUIRED

BID_NOT_ALLOWED
TARGET_ALREADY_REACHED
DUPLICATE_REQUEST
RATE_LIMITED

INSUFFICIENT_BALANCE
INSUFFICIENT_SHOPPING_CREDIT

SHOPPING_WALLET_OPERATION_NOT_ALLOWED

PRODUCT_NOT_FOUND
PRODUCT_NOT_AVAILABLE
OUT_OF_STOCK

ORDER_NOT_FOUND
ORDER_NOT_CANCELLABLE

UNAUTHORIZED
FORBIDDEN
```

---

# 99. PROJECT STRUCTURE

Backend:

```text
src/
│
├── Auction.Api/
│   ├── Controllers/
│   ├── Hubs/
│   ├── Middleware/
│   ├── Extensions/
│   └── Program.cs
│
├── Auction.Application/
│   ├── Auctions/
│   ├── Bidding/
│   ├── Participants/
│   ├── Payments/
│   ├── Wallets/
│   ├── Prizes/
│   ├── Store/
│   ├── Notifications/
│   └── Common/
│
├── Auction.Domain/
│   ├── Entities/
│   ├── ValueObjects/
│   ├── Enums/
│   ├── Events/
│   └── Rules/
│
├── Auction.Infrastructure/
│   ├── Persistence/
│   ├── Identity/
│   ├── Payments/
│   ├── Notifications/
│   ├── Redis/
│   └── Storage/
│
└── Auction.Tests/
    ├── Unit/
    ├── Integration/
    └── Concurrency/
```

---

# 100. FLUTTER STRUCTURE

```text
lib/
│
├── core/
│   ├── network/
│   ├── auth/
│   ├── storage/
│   ├── localization/
│   ├── theme/
│   └── errors/
│
└── features/
    ├── auth/
    ├── home/
    ├── auctions/
    ├── bidding/
    ├── payments/
    ├── wallets/
    ├── store/
    ├── prizes/
    ├── notifications/
    └── profile/
```

Feature layers:

```text
data
domain
presentation
```

Use:

```text
View
↓
ViewModel
↓
Repository
↓
Service
↓
REST / SignalR
```

---

# 101. FLUTTER TECHNOLOGY

Recommended:

```text
Riverpod
Dio
SignalR client
Firebase Messaging
Flutter Secure Storage
```

Arabic must be the default language.

Support:

```text
Arabic
English
```

RTL must be fully supported.

---

# 102. ADMIN PANEL

The admin interface can be implemented as a web application using the existing frontend stack or an appropriate ASP.NET-compatible frontend.

Admin route:

```text
/admin
```

Authentication must be protected by backend authorization.

---

# 103. ADMIN ROLES

Minimum:

```text
USER
ADMIN
SUPER_ADMIN
```

Optional:

```text
AUCTION_MANAGER
FINANCE
SUPPORT
STORE_MANAGER
```

Recommended permissions:

```text
Auction.Read
Auction.Create
Auction.Update
Auction.Start
Auction.Cancel

Store.Read
Store.CreateProduct
Store.UpdateProduct
Store.DeleteProduct
Store.ManageInventory
Store.ManageOrders

Wallet.Read
Wallet.Adjust
Wallet.Refund

User.Read
User.Manage
```

---

# 104. ADMIN LOGIN

Admin login:

```text
/admin
```

must authenticate through the same secure identity system or a dedicated admin authentication flow.

After login:

```text
JWT
+
Role / Permission Claims
```

The backend verifies permissions.

Never rely only on hiding UI elements.

---

# 105. SECURITY

Implement:

```text
HTTPS
JWT
Refresh Token Rotation
Secure Token Storage
Role-Based Authorization
Policy-Based Authorization
Input Validation
Rate Limiting
CORS
Secure Headers
Audit Logging
Payment Webhook Verification
Idempotency
```

---

# 106. RATE LIMITING

Bid endpoints require stricter rate limiting.

Protect against:

```text
Spam
Automated bidding abuse
Duplicate requests
Bot traffic
Request flooding
```

Redis may be used for distributed rate limiting.

---

# 107. AUDIT LOGGING

Create:

```text
AuctionEvent
```

Fields:

```text
EventId
AuctionId
UserId
EventType
Timestamp
OldValue
NewValue
Metadata
IP
UserAgent
```

Events include:

```text
PARTICIPANT_JOINED
REQUIRED_PARTICIPANTS_REACHED
AUCTION_START_SCHEDULED
AUCTION_START_TIME_CHANGED
AUCTION_STARTED_MANUALLY
AUCTION_STARTED
FINAL_PHASE_STARTED
BID_PLACED
TARGET_REACHED
AUCTION_FINISHED
WINNERS_DECLARED

ENTRY_FEE_REFUNDED
SHOPPING_CREDIT_ADDED

STORE_PRODUCT_CREATED
STORE_PRODUCT_UPDATED
INVENTORY_ADJUSTED

STORE_ORDER_CREATED
STORE_ORDER_CANCELLED
STORE_ORDER_REFUNDED
```

---

# 108. BACKGROUND WORKER

Use ASP.NET Core:

```text
BackgroundService
```

Responsibilities:

```text
Start scheduled auctions
Detect expired auctions
Process refunds
Process notifications
Process payment-related jobs
Process order-related background work
```

---

# 109. SCHEDULED AUCTION WORKER

Find:

```text
Status = WAITING_FOR_START
AND
ScheduledStartAt <= UTC_NOW
```

Then atomically transition:

```text
WAITING_FOR_START
→
LIVE
```

This must be safe across multiple backend instances.

Do not allow two instances to start the same auction.

---

# 110. REDIS

Redis may be used for:

```text
Caching
Distributed locks where appropriate
Rate limiting
Temporary state
SignalR scale-out
Session-related transient data
```

Redis must NOT become the permanent financial or auction source of truth.

PostgreSQL remains authoritative.

---

# 111. POSTGRESQL

PostgreSQL stores:

```text
Users
Auctions
Participants
Bids
Winners
Payments
Wallets
Wallet Transactions
Refunds
Products
Categories
Inventory
Orders
Order Items
Notifications
Audit Events
Prize Claims
```

---

# 112. DATABASE CONCURRENCY

Use PostgreSQL transaction mechanisms such as:

```text
Row-level locking
Optimistic concurrency
Atomic updates
Unique constraints
Serializable/appropriate transaction isolation where required
```

The exact strategy must be selected based on the operation.

Never depend on:

```text
lock()
SemaphoreSlim
static dictionary
in-memory mutex
```

as the sole concurrency protection.

---

# 113. STORE INVENTORY CONCURRENCY

Two users attempting to buy the last product must not both succeed.

Example:

```text
Stock = 1

User A buys
User B buys simultaneously
```

Expected:

```text
User A → success
User B → OUT_OF_STOCK
```

The database transaction must enforce this.

---

# 114. WALLET CONCURRENCY

Two simultaneous purchases must not allow spending the same Shopping Wallet balance twice.

Example:

```text
Shopping Wallet = $30

Two simultaneous purchases:
$25
$25
```

Only one may succeed.

Final balance:

```text
$5
```

not:

```text
-$20
```

and not two successful orders.

---

# 115. STORE CREDIT CONCURRENCY

If multiple refund processes run simultaneously:

```text
Same Auction
Same Participant
```

only one Shopping Credit transaction may succeed.

---

# 116. MAIN WALLET WITHDRAWAL

Withdrawal must:

```text
Verify user
Verify Main Wallet
Verify sufficient balance
Create withdrawal transaction
Lock required balance
Submit payment request
Handle provider result
Record final state
```

Shopping Wallet must never be included in withdrawable balance.

---

# 117. FINANCIAL TRANSACTION PRINCIPLE

Never trust:

```text
Flutter balance
Client-calculated price
Client timestamp
Client rank
Client refund amount
Client product price
```

All financial values come from backend/database.

---

# 118. STORE SEARCH

Store should support:

```text
Search
Category
Price range
Availability
Featured
Newest
Popular
```

---

# 119. STORE IMAGES

Products support multiple images.

Recommended:

```text
Primary Image
Gallery Images
Thumbnail
```

Storage can use:

```text
Local storage
S3-compatible object storage
Azure Blob
Other configured provider
```

Do not store large binary images directly inside PostgreSQL unless there is a specific reason.

---

# 120. SHIPPING

Store orders may require:

```text
ShippingAddress
```

Entity:

```text
ShippingAddress

Id
UserId
FullName
Phone
Country
City
Area
AddressLine1
AddressLine2
PostalCode
CreatedAt
```

Do not expose unnecessary address information to unrelated administrators.

---

# 121. ADMIN STORE ORDER WORKFLOW

```text
PAID
 ↓
PROCESSING
 ↓
SHIPPED
 ↓
DELIVERED
 ↓
COMPLETED
```

Cancellation:

```text
PAID / PROCESSING
 ↓
CANCELLED
 ↓
REFUND
```

Exact transitions must be validated.

---

# 122. STORE CREDIT BUSINESS MODEL

The store credit creates a closed-loop ecosystem:

```text
User pays auction entry fee
        ↓
User loses
        ↓
Entry fee converted to Shopping Credit
        ↓
User shops
        ↓
Shopping Credit spent
        ↓
Platform sells store products
```

The credit is not equivalent to cash and must remain separately tracked.

---

# 123. IMPORTANT CURRENCY RULE

Although examples use USD:

```text
$20
```

the system must support configurable currency.

Store:

```text
Amount
Currency
```

Do not assume every amount is USD.

---

# 124. INTERNATIONALIZATION

Support:

```text
Arabic
English
```

Arabic:

```text
RTL
```

English:

```text
LTR
```

All user-facing strings must be localized.

Do not hardcode Arabic strings directly inside business logic.

---

# 125. OBSERVABILITY

Implement:

```text
Serilog
OpenTelemetry
Metrics
Tracing
Structured Logging
```

Health endpoints:

```text
/health
/health/live
/health/ready
```

Track important metrics:

```text
Active Auctions
Bids Per Second
Bid Rejections
Auction Completion Time
Concurrent Bids
Wallet Transactions
Refunds
Store Orders
Out of Stock Events
Payment Failures
SignalR Connections
```

---

# 126. TESTING STRATEGY

Required:

```text
Unit Tests
Integration Tests
Database Tests
Concurrency Tests
API Tests
Authorization Tests
Wallet Tests
Store Tests
SignalR Tests
```

---

# 127. CRITICAL AUCTION CONCURRENCY TEST

Scenario:

```text
Current = $2,480
Target = $2,500
Increment = $20
```

Send:

```text
100 simultaneous bids
```

Expected:

```text
Exactly one bid reaches target.

Auction:
FINISHED

Current Price:
$2,500

Post-close bids:
0 successful

No duplicate sequence numbers.

No duplicate target event.
```

---

# 128. CRITICAL WALLET TEST

Scenario:

```text
Shopping Wallet = $30
```

Two simultaneous:

```text
$25 purchases
```

Expected:

```text
One success
One insufficient balance
Final balance = $5
```

---

# 129. CRITICAL INVENTORY TEST

Scenario:

```text
Stock = 1
```

Two simultaneous purchases.

Expected:

```text
One success
One OUT_OF_STOCK
Stock = 0
```

---

# 130. CRITICAL REFUND TEST

Scenario:

```text
Entry Fee = $20
Participant loses
```

Expected:

```text
Main Wallet refund = $0
Shopping Wallet = +$20
```

Repeat refund request 100 times.

Expected:

```text
Shopping Wallet = +$20 exactly once
```

---

# 131. DUPLICATE PAYMENT TEST

Repeated payment webhook:

```text
same ProviderTransactionId
```

Expected:

```text
Only one financial transaction.
```

---

# 132. STORE PURCHASE TEST

Scenario:

```text
Shopping Wallet = $50
Product = $35
```

Expected:

```text
Order = PAID
Shopping Wallet = $15
Wallet transaction = -$35
```

---

# 133. WITHDRAWAL RESTRICTION TEST

Scenario:

```text
Shopping Wallet = $100
```

Attempt:

```text
Withdrawal = $100
```

Expected:

```text
Rejected
```

Main Wallet must remain unchanged.

---

# 134. ADMIN AUDIT TEST

Every admin operation such as:

```text
Create Product
Edit Product
Adjust Inventory
Issue Shopping Credit
Cancel Order
Refund Order
Start Auction
Change Auction Schedule
```

must produce an audit event.

---

# 135. DATABASE MIGRATIONS

Use EF Core migrations.

Commands must be documented.

Never manually alter production schema without a controlled migration.

Seed only development/test data automatically.

---

# 136. DEVELOPMENT SEED DATA

Development environment may include:

```text
Admin
Super Admin
Test Users
Products
Store Products
Categories
Sample Auctions
```

Never use default development credentials in production.

---

# 137. CONFIGURATION

Use:

```text
appsettings.json
appsettings.Development.json
Environment Variables
User Secrets
Secret Manager
```

Never commit:

```text
JWT secret
Database password
Payment keys
Firebase credentials
API keys
```

to Git.

---

# 138. DOCKER

The system must be Docker-ready.

Recommended services:

```text
api
postgres
redis
```

Optional:

```text
admin-web
nginx
worker
```

API must remain stateless to support multiple instances.

---

# 139. MULTI-INSTANCE REQUIREMENT

The application must work correctly with:

```text
API Instance 1
API Instance 2
API Instance 3
```

without changing auction behavior.

No critical business logic may depend on process-local memory.

---

# 140. CACHING

Cache only safe/read-heavy data.

Examples:

```text
Store categories
Store product catalog
Auction public metadata
```

Do NOT blindly cache:

```text
Wallet balance
Current auction price
Winner state
Payment state
```

unless invalidation/consistency is explicitly designed.

---

# 141. ADMIN DASHBOARD — COMPLETE NAVIGATION

```text
ADMIN
│
├── Dashboard
│
├── Auctions
│   ├── All Auctions
│   ├── Create Auction
│   ├── Active
│   ├── Upcoming
│   ├── Completed
│   ├── Participants
│   └── Winners
│
├── Products
│
├── Store
│   ├── Overview
│   ├── Products
│   ├── Categories
│   ├── Inventory
│   ├── Orders
│   ├── Customers
│   ├── Shopping Credits
│   └── Settings
│
├── Payments
│
├── Wallets
│   ├── Main Wallets
│   ├── Shopping Wallets
│   ├── Transactions
│   └── Refunds
│
├── Users
│
├── Notifications
│
├── Reports
│
├── Audit Logs
│
└── Settings
```

---

# 142. ADMIN DASHBOARD KPIs

Main dashboard:

```text
Active Auctions
Upcoming Auctions
Total Participants
Today's Entry Revenue
Total Entry Revenue
Pending Prize Payments

Shopping Credit Issued
Shopping Credit Used

Store Orders
Store Revenue
Pending Orders

Main Wallet Deposits
Main Wallet Withdrawals
```

---

# 143. REPORTS

Reports should include:

```text
Auction Performance
Participant Statistics
Entry Fee Revenue
Winner Statistics
Prize Payments

Shopping Credit Issued
Shopping Credit Redemption
Store Sales
Product Sales
Inventory

Wallet Deposits
Wallet Withdrawals
Financial Transactions
```

Export may support:

```text
CSV
Excel
PDF
```

depending on implementation requirements.

---

# 144. DOMAIN MODULES

Backend modular monolith:

```text
Identity
Users
Auctions
Bidding
Participants
Payments
Wallets
Prizes
Store
Inventory
Orders
Notifications
Administration
Audit
```

Do not split into microservices initially unless scale/business requirements justify it.

---

# 145. APPLICATION LAYER

Use command/query separation where useful.

Examples:

```text
CreateAuctionCommand
PublishAuctionCommand
JoinAuctionCommand
PlaceBidCommand
StartAuctionCommand
FinishAuctionCommand
DeclareWinnersCommand

CreditShoppingWalletCommand
CreateStoreProductCommand
PurchaseStoreProductCommand
AdjustInventoryCommand
RefundStoreOrderCommand
```

---

# 146. DOMAIN LAYER

Domain should contain business rules such as:

```text
AuctionState transitions
Bid validation
Target calculation
Final phase rules
Ranking rules
Refund eligibility
Wallet restrictions
Order state transitions
Inventory rules
```

Controllers must not contain these rules.

---

# 147. API CONTROLLERS

Controllers should be thin.

They should:

```text
Validate request
Authenticate/authorize
Call application service/command
Return DTO
```

Do not place complex business logic inside controllers.

---

# 148. DTOs

Never expose EF entities directly.

Use:

```text
Request DTOs
Response DTOs
View Models
```

---

# 149. DATA CONSISTENCY

Critical transactions:

```text
Join Auction
Place Bid
Finish Auction
Declare Winners
Refund Entry Fees
Credit Wallet
Purchase Store Product
Adjust Inventory
Pay Prize
Withdraw Funds
```

must have explicit transactional boundaries.

---

# 150. EVENT-DRIVEN INTERNAL PROCESSING

Use domain/application events where appropriate.

Example:

```text
AuctionFinished
        ↓
WinnersDeclared
        ↓
RefundLosingParticipants
        ↓
ShoppingCreditAdded
        ↓
Notifications
```

Do not allow notifications to determine whether a financial transaction succeeded.

---

# 151. AUCTION EVENT ORDER

Recommended event sequence:

```text
PARTICIPANT_JOINED

REQUIRED_PARTICIPANTS_REACHED

AUCTION_START_SCHEDULED

AUCTION_START_TIME_CHANGED

AUCTION_STARTED

FINAL_PHASE_STARTED

BID_PLACED

TARGET_REACHED

AUCTION_FINISHED

WINNERS_DECLARED

ENTRY_FEE_REFUNDED

SHOPPING_CREDIT_ADDED

PRIZE_CLAIM_AVAILABLE
```

---

# 152. USER EXPERIENCE AFTER LOSING

After winners are declared:

```text
Auction Finished

You did not finish in the top 4.

Your $20 entry fee has been returned
as Shopping Credit.

Shopping Credit:
$20

[ SHOP NOW ]
```

This must clearly state that the amount is:

```text
Shopping Credit
```

not withdrawable cash.

---

# 153. USER EXPERIENCE AFTER WINNING

Example:

```text
🎉 Congratulations!

You finished #2.

Prize:
Product 2

Prize Value:
$1,200

Payment Required:
$1,200

[ PAY FOR PRIZE ]
```

---

# 154. STORE CREDIT EXPIRATION

Make expiration configurable.

Settings:

```text
ShoppingCreditExpirationEnabled
ShoppingCreditExpirationDays
```

If enabled, clearly show expiration dates to users.

Do not silently expire credits.

If disabled:

```text
No expiration
```

---

# 155. STORE CREDIT SOURCE TRACKING

Every Shopping Wallet credit must have a source.

Example:

```text
Auction #123
Entry Fee Refund
+$20
```

Other possible sources:

```text
ADMIN_ADJUSTMENT
STORE_REFUND
PROMOTION
BONUS
```

This allows complete accounting.

---

# 156. SHOPPING WALLET HISTORY

User can view:

```text
Shopping Credit Added
Store Purchase
Store Refund
Admin Adjustment
Expiration
```

Example:

```text
07 Sep
Auction Entry Refund
+$20

07 Sep
Store Purchase
-$15

Balance:
$5
```

---

# 157. MAIN WALLET HISTORY

Show:

```text
Deposit
Withdrawal
Auction Entry Fee
Prize Payment
Refund
Adjustment
```

Keep it completely separate from Shopping Wallet history.

---

# 158. STORE PRODUCT VISIBILITY

Admin can control:

```text
Active
Hidden
Archived
Out of Stock
```

Hidden products must not appear in public store catalog.

Archived products remain available for historical order references.

---

# 159. PRODUCT PRICE CHANGES

Changing a product price must NOT modify existing orders.

Existing order items use:

```text
ProductNameSnapshot
UnitPrice
```

---

# 160. PRODUCT DELETION

Do not physically delete products referenced by orders.

Use:

```text
ARCHIVED
```

instead.

---

# 161. AUCTION PRODUCT SNAPSHOTS

Auction records should preserve:

```text
OriginalPrice
Product Name
Relevant Product Information
```

so historical auctions do not change if the product is later edited.

---

# 162. ADMIN PERMISSION FOR SHOP

Only users with appropriate permissions can:

```text
Create Product
Edit Product
Manage Inventory
Manage Orders
Issue Shopping Credits
Refund Orders
```

---

# 163. ADMIN SUPER ADMIN

SUPER_ADMIN can:

```text
Manage admins
Manage permissions
Manage system settings
Manage financial adjustments
```

Dangerous operations must require elevated authorization.

---

# 164. AUDIT IMMUTABILITY

Financial and audit records should be append-only whenever practical.

Do not allow ordinary admins to delete:

```text
Wallet Transactions
Bid History
Payment Records
Refund Records
Audit Logs
Order Financial Records
```

---

# 165. SECURITY AGAINST CLIENT MANIPULATION

Never accept from Flutter:

```text
currentPrice
targetPrice
bidAmount
rank
winner
refundAmount
walletBalance
productPrice
```

The server obtains authoritative values from the database.

---

# 166. MOBILE OFFLINE BEHAVIOR

The app may cache public data, but bidding requires a live authoritative connection.

If disconnected:

```text
Disable/grey bid button
Show connection status
Reconnect automatically
Resync auction snapshot
```

Never queue bids blindly for later execution.

---

# 167. REAL-TIME TIMER

The backend provides authoritative timestamps.

Flutter may calculate the displayed countdown using:

```text
server time
+
known end time
```

Do not use device local time as authoritative.

---

# 168. PERFORMANCE

The system should support:

```text
High concurrent connections
High bid frequency
Multiple active auctions
Multiple API instances
Multiple SignalR nodes
```

Optimize:

```text
Database indexes
SignalR groups
Redis
Connection handling
EF Core queries
Pagination
DTO projections
```

---

# 169. DATABASE INDEXES

Add appropriate indexes for:

```text
Auction.Status
Auction.ScheduledStartAt
Auction.FinishedAt

Bid.AuctionId
Bid.AuctionId + SequenceNumber

AuctionParticipant.AuctionId
AuctionParticipant.UserId

Wallet.UserId
Wallet.UserId + WalletType

WalletTransaction.UserId
WalletTransaction.ReferenceId

StoreProduct.Status
StoreProduct.CategoryId
StoreProduct.SKU

StoreOrder.UserId
StoreOrder.Status
StoreOrder.CreatedAt

Notification.UserId
Notification.ReadAt
```

---

# 170. PAGINATION

Admin lists must support server-side pagination:

```text
Products
Orders
Participants
Users
Transactions
Audit Logs
Auctions
```

Do not load thousands of records into the browser at once.

---

# 171. API DOCUMENTATION

Generate:

```text
Swagger/OpenAPI
```

Document:

```text
Authentication
Authorization
Auction API
Bidding
Wallet
Store
Orders
Prize Claims
Admin APIs
Error Codes
```

---

# 172. API VERSIONING

Use:

```text
/api/v1
```

Future breaking changes should use:

```text
/api/v2
```

---

# 173. CI/CD

Pipeline:

```text
Restore
↓
Build
↓
Unit Tests
↓
Integration Tests
↓
Concurrency Tests
↓
Security Checks
↓
Docker Build
↓
Migration Validation
↓
Deploy
```

---

# 174. BACKUP

PostgreSQL must have:

```text
Automated backups
Point-in-time recovery where available
Backup verification
Restore testing
```

Financial records must not depend on Redis or client state.

---

# 175. DISASTER RECOVERY

Document:

```text
Database restore
Redis recovery
API recovery
SignalR reconnection
Payment webhook recovery
Refund retry
Order recovery
```

---

# 176. IDEMPOTENCY AREAS

Idempotency must be considered for:

```text
Bids
Payments
Refunds
Wallet credits
Store purchases
Order creation
Payment webhooks
Admin financial operations
```

---

# 177. PAYMENT WEBHOOKS

Every webhook must verify:

```text
Signature
Provider
Transaction ID
Amount
Currency
Status
```

Repeated webhook delivery must be safe.

---

# 178. STORE PURCHASE IDEMPOTENCY

Checkout request should support an idempotency key.

If client retries:

```text
same idempotency key
```

the backend must return the existing order/result rather than creating another purchase.

---

# 179. ORDER NUMBER

Generate human-friendly order numbers:

```text
ORD-2026-000001
```

The actual database ID remains internal.

---

# 180. AUCTION NUMBER

Optionally generate:

```text
AUC-2026-000001
```

for admin/customer-friendly identification.

---

# 181. PRODUCT SKU

Every store product should have a unique SKU:

```text
SKU-HEADPHONES-001
```

Unique database constraint required.

---

# 182. ADMIN STORE SEARCH

Admin should be able to search:

```text
Product name
SKU
Category
Order number
Customer
```

---

# 183. ADMIN STORE FILTERS

Filters:

```text
Active
Hidden
Archived
Out of Stock
Low Stock
```

Orders:

```text
Pending
Paid
Processing
Shipped
Delivered
Completed
Cancelled
Refunded
```

---

# 184. LOW STOCK ALERT

Configurable threshold:

```text
LowStockThreshold = 5
```

Admin receives notification:

```text
⚠️ Low Stock

Headphones Pro

Remaining:
4
```

---

# 185. STORE FEATURED PRODUCTS

Admin can mark:

```text
Featured
```

Products.

Store home displays featured products.

---

# 186. STORE PRODUCT PROMOTIONS

Optional future feature:

```text
Discount
Promo Code
Sale Price
Start Date
End Date
```

Do not compromise Shopping Wallet accounting.

---

# 187. DISCOUNT RULE

Discount must be calculated by backend.

Flutter never determines final order total.

---

# 188. ORDER TOTAL

Backend calculates:

```text
Subtotal
Discount
Shipping
Tax
Total
```

Then verifies payment.

---

# 189. FINANCIAL ROUNDING

Use deterministic decimal rounding.

Currency rules must be configurable.

Never use floating-point arithmetic.

---

# 190. DATA RETENTION

Financial records should be retained according to applicable business/legal requirements.

Do not casually delete:

```text
Payments
Wallet Transactions
Refunds
Orders
Auction Bids
Winner Records
Audit Logs
```

---

# 191. ADMIN SECURITY

Admin dashboard must use:

```text
HTTPS
Short-lived access token
Refresh rotation
Role authorization
Permission checks
Rate limiting
Audit logging
Optional MFA
```

---

# 192. MFA

Recommended for:

```text
SUPER_ADMIN
FINANCE
Admins performing financial adjustments
```

---

# 193. ADMIN FINANCIAL OPERATIONS

Operations such as:

```text
Manual Wallet Adjustment
Manual Shopping Credit
Refund
Prize Payment Adjustment
```

must require:

```text
Reason
Admin identity
Timestamp
Permission
Audit event
```

---

# 194. USER PROFILE

User profile:

```text
Name
Email
Phone
Language
Notification Preferences
Addresses
Wallets
Auction History
Orders
Prize Claims
```

---

# 195. USER AUCTION HISTORY

Show:

```text
Participated Auctions
Won Auctions
Lost Auctions
Entry Fees
Shopping Credits Received
Highest Bid
Final Rank
Prize
```

---

# 196. USER STORE HISTORY

Show:

```text
Orders
Order Status
Purchased Products
Shopping Wallet Payments
Shipping
```

---

# 197. BUSINESS FLOW — COMPLETE

```text
ADMIN CREATES AUCTION
        ↓
4 PRIZES ASSIGNED
        ↓
AUCTION PUBLISHED
        ↓
REGISTRATION OPENS
        ↓
USERS PAY ENTRY FEE
        ↓
PARTICIPANTS JOIN
        ↓
400/400
        ↓
START DELAY
        ↓
AUCTION READY
        ↓
ADMIN MAY START NOW
OR
SCHEDULED START
        ↓
AUCTION LIVE
        ↓
BIDS
        ↓
FINAL PHASE
        ↓
TARGET REACHED
        ↓
AUCTION FINISHED
        ↓
WINNERS DECLARED
        ↓
TOP 4 RECEIVE PRIZES
        ↓
LOSERS RECEIVE ENTRY FEE
AS SHOPPING CREDIT
        ↓
USER OPENS STORE
        ↓
SHOPPING CREDIT USED
        ↓
STORE ORDER
        ↓
INVENTORY DECREASED
        ↓
ORDER FULFILLED
```

---

# 198. FINAL ARCHITECTURE

```text
                     ┌──────────────────────┐
                     │      Flutter App     │
                     │                      │
                     │ Auctions             │
                     │ Bidding              │
                     │ Wallet               │
                     │ Store                │
                     │ Orders               │
                     └──────────┬───────────┘
                                │
                         HTTPS / SignalR
                                │
                                ▼
                     ┌──────────────────────┐
                     │ ASP.NET Core API     │
                     │                      │
                     │ Identity             │
                     │ Auctions             │
                     │ Bidding              │
                     │ Wallets              │
                     │ Payments             │
                     │ Store                │
                     │ Inventory            │
                     │ Orders               │
                     │ Prizes               │
                     │ Notifications         │
                     │ Administration       │
                     └───────┬───────┬──────┘
                             │       │
                  ┌──────────┘       └──────────┐
                  ▼                             ▼
          ┌──────────────┐              ┌──────────────┐
          │ PostgreSQL   │              │ Redis        │
          │              │              │              │
          │ Source Truth │              │ Cache        │
          │ Financials   │              │ Rate Limit   │
          │ Auctions     │              │ SignalR      │
          │ Bids         │              │ Coordination │
          │ Wallets      │              └──────────────┘
          │ Store        │
          │ Orders       │
          └──────────────┘
```

---

# 199. IMPLEMENTATION PHASES

Implement in this order.

## Phase 1 — Repository Analysis

Inspect existing system.

Deliver:

```text
Architecture assessment
Existing functionality
Conflicts
Missing functionality
Migration plan
```

---

## Phase 2 — Foundation

Implement/verify:

```text
ASP.NET Core
PostgreSQL
EF Core
JWT
Redis
Swagger
Serilog
Health Checks
Docker
```

---

## Phase 3 — Identity

Implement:

```text
Register
Login
Refresh
Logout
Roles
Permissions
Admin authentication
```

---

## Phase 4 — Auction Domain

Implement:

```text
Auction
AuctionPrize
Participant
Bid
Winner
PrizeClaim
```

---

## Phase 5 — Auction Engine

Implement:

```text
State machine
Participant threshold
Start delay
Scheduled start
Manual start
Duration
Target
Final phase
Bid engine
Concurrency
Ranking
Winner declaration
```

---

## Phase 6 — Real-Time

Implement:

```text
SignalR
Auction groups
Bid events
Rank events
Timer synchronization
Reconnect
Snapshot synchronization
```

---

## Phase 7 — Main Wallet

Implement:

```text
Wallet
Ledger
Deposit
Withdrawal
Entry Fee
Prize Payment
```

---

## Phase 8 — Shopping Wallet

Implement:

```text
Shopping Wallet
Credit ledger
Auction refund
Refund idempotency
Credit history
Restrictions
```

---

## Phase 9 — Store

Implement:

```text
Products
Categories
Images
Inventory
Orders
Order Items
Checkout
Shopping Wallet payment
```

---

## Phase 10 — Admin Store

Implement:

```text
Store Overview
Products
Categories
Inventory
Orders
Customers
Shopping Credits
Settings
```

---

## Phase 11 — Notifications

Implement:

```text
FCM
In-app notifications
Auction notifications
Refund notifications
Store notifications
Order notifications
```

---

## Phase 12 — Flutter

Implement:

```text
Authentication
Home
Auctions
Live Auction
Wallets
Store
Checkout
Orders
Prize Claims
Notifications
Profile
```

---

## Phase 13 — Security

Audit:

```text
Authorization
Rate limits
JWT
Secrets
CORS
Payment webhooks
Idempotency
Input validation
Financial operations
Admin permissions
```

---

## Phase 14 — Testing

Run:

```text
Unit
Integration
Concurrency
Wallet
Inventory
Refund
Payment
Authorization
SignalR
```

---

## Phase 15 — Production

Implement:

```text
Docker
CI/CD
Backups
Monitoring
Logging
Metrics
Tracing
Health checks
Production configuration
```

---

# 200. DEFINITION OF DONE

The implementation is NOT considered complete until:

### Auction

```text
✓ Exactly 4 prizes
✓ Required participant threshold
✓ Start delay
✓ Manual start
✓ Scheduled start
✓ Live bidding
✓ Atomic bid engine
✓ Target detection
✓ Final phase
✓ Ranking
✓ Top 4 winners
✓ Auction completion
```

### Wallet

```text
✓ Main Wallet
✓ Shopping Wallet
✓ Ledger
✓ Entry fee
✓ Withdrawals
✓ Prize payments
✓ Losing participant credit
✓ No Shopping Wallet withdrawal
```

### Store

```text
✓ Products
✓ Categories
✓ Images
✓ Inventory
✓ Orders
✓ Checkout
✓ Shopping Wallet payment
✓ Admin product management
✓ Admin inventory management
✓ Admin order management
✓ Shopping credit management
```

### Real-time

```text
✓ SignalR
✓ Reconnect
✓ Snapshot synchronization
✓ Live price
✓ Live ranking
✓ Participant count
✓ Final phase
```

### Security

```text
✓ JWT
✓ Refresh rotation
✓ RBAC
✓ Permissions
✓ Rate limiting
✓ Audit logs
✓ Idempotency
✓ Payment webhook verification
```

### Quality

```text
✓ Unit tests
✓ Integration tests
✓ Concurrency tests
✓ Docker
✓ Database migrations
✓ Swagger
✓ Health checks
✓ Logging
✓ Documentation
```

---

# 201. REQUIRED DOCUMENTATION

Create:

```text
README.md
ARCHITECTURE.md
API.md
AUCTION_ENGINE.md
DATABASE.md
WALLETS.md
STORE.md
PAYMENTS.md
SECURITY.md
DEPLOYMENT.md
```

---

# 202. FINAL DEVELOPMENT PRINCIPLE

The most important rule:

> **The database and backend are the authority. The Flutter client is only a client.**

For auctions:

```text
Server decides price.
Server decides time.
Server decides rank.
Server decides winners.
```

For wallets:

```text
Server decides balance.
Server decides refunds.
Server decides withdrawal eligibility.
```

For store:

```text
Server decides product price.
Server decides stock.
Server decides order total.
Server decides payment success.
```

For administration:

```text
Server decides permissions.
Server records every sensitive operation.
```

Never trust the client for business-critical state.
