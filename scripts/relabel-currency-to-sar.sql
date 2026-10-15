-- Relabel the currency on an existing development database from USD to SAR.
--
-- IMPORTANT: this is a RENAME, not a CONVERSION. The amounts are left exactly as they are,
-- so a balance of 500 "USD" becomes a balance of 500 "SAR". That is correct for seeded test
-- money, and WRONG for real money. On a database with real balances, multiply by the rate
-- inside the same transaction, or leave the old rows alone and let new rows use SAR.
--
-- Every table that stores money stores its own currency beside it. A wallet movement whose
-- currency does not match the wallet is refused with CURRENCY_MISMATCH, so a half-finished
-- relabel would break checkout and withdrawal. That is why this runs as one transaction.
--
-- Run with:
--   docker exec -i auction-postgres psql -U postgres -d auctions -v ON_ERROR_STOP=1 \
--     < scripts/relabel-currency-to-sar.sql

BEGIN;

UPDATE "AuctionEntryFeeRefunds" SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "AuctionParticipants"    SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "Auctions"               SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "Payments"               SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "PrizeClaims"            SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "Products"               SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "StoreOrders"            SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "StoreProducts"          SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "WalletTransactions"     SET "Currency" = 'SAR' WHERE "Currency" = 'USD';
UPDATE "Wallets"                SET "Currency" = 'SAR' WHERE "Currency" = 'USD';

-- The store's own currency. It does NOT use a column called "Currency", which is exactly why
-- it was missed the first time. Checkout compares this against the wallet, so leaving it on USD
-- makes every store purchase fail with CURRENCY_MISMATCH — with either wallet.
UPDATE "StoreSettings" SET "DefaultCurrency" = 'SAR' WHERE "DefaultCurrency" = 'USD';

-- The seeded demo names were English. New databases seed these in Arabic already; this
-- brings an existing one into line. Slugs and SKUs are deliberately untouched: they belong
-- in URLs and stay ASCII.
UPDATE "Products" SET "Name" = 'سيارة دفع رباعي فاخرة'  WHERE "Name" = 'Luxury SUV';
UPDATE "Products" SET "Name" = 'حاسب محمول متميز'       WHERE "Name" = 'Premium Laptop';
UPDATE "Products" SET "Name" = 'هاتف ذكي برو'           WHERE "Name" = 'Smartphone Pro';
UPDATE "Products" SET "Name" = 'ساعة ذكية'              WHERE "Name" = 'Smart Watch';
UPDATE "Products" SET "Name" = 'مسرح منزلي'             WHERE "Name" = 'Home Theatre';
UPDATE "Products" SET "Name" = 'جهاز ألعاب'             WHERE "Name" = 'Gaming Console';
UPDATE "Products" SET "Name" = 'سكوتر كهربائي'          WHERE "Name" = 'Electric Scooter';
UPDATE "Products" SET "Name" = 'آلة إسبريسو'            WHERE "Name" = 'Espresso Machine';

UPDATE "StoreCategories" SET "Name" = 'إلكترونيات' WHERE "Slug" = 'electronics';
UPDATE "StoreCategories" SET "Name" = 'إكسسوارات'  WHERE "Slug" = 'accessories';
UPDATE "StoreCategories" SET "Name" = 'المنزل'      WHERE "Slug" = 'home';

UPDATE "StoreProducts" SET "Name" = 'سماعات رأس برو'   WHERE "Slug" = 'headphones-pro';
UPDATE "StoreProducts" SET "Name" = 'سماعات لاسلكية'   WHERE "Slug" = 'wireless-earbuds';
UPDATE "StoreProducts" SET "Name" = 'مكبر صوت بلوتوث'  WHERE "Slug" = 'bluetooth-speaker';
UPDATE "StoreProducts" SET "Name" = 'غطاء هاتف'        WHERE "Slug" = 'phone-case';
UPDATE "StoreProducts" SET "Name" = 'شاحن سريع'        WHERE "Slug" = 'fast-charger';
UPDATE "StoreProducts" SET "Name" = 'حقيبة حاسب محمول' WHERE "Slug" = 'laptop-sleeve';
UPDATE "StoreProducts" SET "Name" = 'مصباح مكتب'       WHERE "Slug" = 'desk-lamp';
UPDATE "StoreProducts" SET "Name" = 'مطحنة قهوة'       WHERE "Slug" = 'coffee-grinder';
UPDATE "StoreProducts" SET "Name" = 'ميزان مطبخ'       WHERE "Slug" = 'kitchen-scale';

UPDATE "Auctions" SET "Title" = 'مزاد السيارة الفاخرة الكبير', "MainProductNameSnapshot" = 'سيارة دفع رباعي فاخرة'
  WHERE "Title" = 'Luxury SUV Grand Auction';
UPDATE "Auctions" SET "Title" = 'باقة الإلكترونيات المتميزة', "MainProductNameSnapshot" = 'مسرح منزلي'
  WHERE "Title" = 'Premium Electronics Bundle';

COMMIT;

-- Nothing below should return a row.
SELECT 'Wallets'            AS "table", count(*) FROM "Wallets"            WHERE "Currency" = 'USD'
UNION ALL SELECT 'Auctions',            count(*) FROM "Auctions"           WHERE "Currency" = 'USD'
UNION ALL SELECT 'StoreProducts',       count(*) FROM "StoreProducts"      WHERE "Currency" = 'USD'
UNION ALL SELECT 'WalletTransactions',  count(*) FROM "WalletTransactions" WHERE "Currency" = 'USD'
UNION ALL SELECT 'StoreSettings',       count(*) FROM "StoreSettings"      WHERE "DefaultCurrency" = 'USD';
