using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProviderOrderId",
                table: "Payments",
                type: "bigint",
                nullable: true);

            // Top-ups opened before this column existed kept their Paymob order id in Metadata.
            migrationBuilder.Sql("""
                UPDATE "Payments"
                SET "ProviderOrderId" = CAST(substring("Metadata" FROM 'paymob-order:([0-9]+)') AS bigint)
                WHERE "Provider" = 'PAYMOB' AND "ProviderOrderId" IS NULL AND "Metadata" LIKE 'paymob-order:%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Provider_ProviderOrderId",
                table: "Payments",
                columns: new[] { "Provider", "ProviderOrderId" },
                unique: true,
                filter: "\"ProviderOrderId\" IS NOT NULL");

            /*
             * The ledger guards. WalletTransactions is the record of every riyal that moved: a row is
             * never deleted, and none of its financial facts change after it is written; a mistake
             * is corrected by a new, reversing entry. The one column that may change is ExpiresAt,
             * which the shopping-credit expiry worker clears once it has expired a credit.
             * Payments may change status as they progress, but never their amount, currency,
             * owner, type or provider, and a payment is never deleted.
             *
             * Created inside a guarded block: a shared host that refuses CREATE FUNCTION must not
             * stop the site from starting. The monitoring endpoint reports whether the guards exist.
             */
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                    EXECUTE $fn$
                    CREATE OR REPLACE FUNCTION dealgo_wallet_ledger_guard() RETURNS trigger
                    LANGUAGE plpgsql AS $body$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'WalletTransactions is an append-only ledger; record a reversing entry instead of deleting %', OLD."Id";
                        END IF;
                        IF NEW."Id" <> OLD."Id"
                           OR NEW."WalletId" <> OLD."WalletId"
                           OR NEW."UserId" <> OLD."UserId"
                           OR NEW."WalletType" <> OLD."WalletType"
                           OR NEW."TransactionType" <> OLD."TransactionType"
                           OR NEW."Amount" <> OLD."Amount"
                           OR NEW."Currency" <> OLD."Currency"
                           OR NEW."BalanceBefore" <> OLD."BalanceBefore"
                           OR NEW."BalanceAfter" <> OLD."BalanceAfter"
                           OR NEW."ReferenceType" IS DISTINCT FROM OLD."ReferenceType"
                           OR NEW."ReferenceId" IS DISTINCT FROM OLD."ReferenceId"
                           OR NEW."CreditSource" IS DISTINCT FROM OLD."CreditSource"
                           OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                           OR NEW."CreatedAt" <> OLD."CreatedAt" THEN
                            RAISE EXCEPTION 'WalletTransactions % is immutable; only ExpiresAt may change', OLD."Id";
                        END IF;
                        RETURN NEW;
                    END
                    $body$;
                    $fn$;

                    EXECUTE $fn$
                    CREATE OR REPLACE FUNCTION dealgo_payment_guard() RETURNS trigger
                    LANGUAGE plpgsql AS $body$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'Payments are never deleted (%)', OLD."Id";
                        END IF;
                        IF NEW."Amount" <> OLD."Amount"
                           OR NEW."Currency" <> OLD."Currency"
                           OR NEW."UserId" <> OLD."UserId"
                           OR NEW."PaymentType" <> OLD."PaymentType"
                           OR NEW."Provider" <> OLD."Provider"
                           OR (OLD."ProviderTransactionId" IS NOT NULL
                               AND NEW."ProviderTransactionId" IS DISTINCT FROM OLD."ProviderTransactionId") THEN
                            RAISE EXCEPTION 'Payment % changed a fact that is fixed once recorded', OLD."Id";
                        END IF;
                        RETURN NEW;
                    END
                    $body$;
                    $fn$;

                    EXECUTE 'DROP TRIGGER IF EXISTS trg_wallet_ledger_guard ON "WalletTransactions"';
                    EXECUTE 'CREATE TRIGGER trg_wallet_ledger_guard BEFORE UPDATE OR DELETE ON "WalletTransactions" FOR EACH ROW EXECUTE FUNCTION dealgo_wallet_ledger_guard()';
                    EXECUTE 'DROP TRIGGER IF EXISTS trg_payment_guard ON "Payments"';
                    EXECUTE 'CREATE TRIGGER trg_payment_guard BEFORE UPDATE OR DELETE ON "Payments" FOR EACH ROW EXECUTE FUNCTION dealgo_payment_guard()';
                EXCEPTION WHEN insufficient_privilege THEN
                    RAISE WARNING 'Ledger guards not installed: this database user may not create functions or triggers.';
                END
                $do$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_wallet_ledger_guard ON "WalletTransactions";
                DROP TRIGGER IF EXISTS trg_payment_guard ON "Payments";
                DROP FUNCTION IF EXISTS dealgo_wallet_ledger_guard();
                DROP FUNCTION IF EXISTS dealgo_payment_guard();
                """);

            migrationBuilder.DropIndex(
                name: "IX_Payments_Provider_ProviderOrderId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderOrderId",
                table: "Payments");
        }
    }
}
