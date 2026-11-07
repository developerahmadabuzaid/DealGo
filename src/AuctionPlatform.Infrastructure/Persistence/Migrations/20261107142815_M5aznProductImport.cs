using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5aznProductImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoreProducts_M5aznProductId",
                table: "StoreProducts");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryTime",
                table: "StoreProducts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "M5aznRemovedAt",
                table: "StoreProducts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "M5aznSyncedAt",
                table: "StoreProducts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "StoreProducts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryTime",
                table: "M5aznSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "1-5 أيام");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastImportAt",
                table: "M5aznSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastImportSummary",
                table: "M5aznSettings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            // Only products imported from M5azn go to M5azn now. A product staff added in the
            // dashboard and linked by hand (there should be none in production) becomes DealGo's
            // own again; an order already waiting for it is cancelled at M5azn's end of the queue
            // ("No M5azn products on the order any more"), never sent.
            migrationBuilder.Sql("""
                UPDATE "StoreProducts"
                SET "M5aznProductId" = NULL, "M5aznSku" = NULL, "M5aznCostPrice" = NULL
                WHERE "Source" = 0 AND "M5aznProductId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_M5aznProductId",
                table: "StoreProducts",
                column: "M5aznProductId",
                unique: true,
                filter: "\"Source\" = 1 AND \"M5aznProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_Source",
                table: "StoreProducts",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoreProducts_M5aznProductId",
                table: "StoreProducts");

            migrationBuilder.DropIndex(
                name: "IX_StoreProducts_Source",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "DeliveryTime",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "M5aznRemovedAt",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "M5aznSyncedAt",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "DeliveryTime",
                table: "M5aznSettings");

            migrationBuilder.DropColumn(
                name: "LastImportAt",
                table: "M5aznSettings");

            migrationBuilder.DropColumn(
                name: "LastImportSummary",
                table: "M5aznSettings");

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_M5aznProductId",
                table: "StoreProducts",
                column: "M5aznProductId",
                filter: "\"M5aznProductId\" IS NOT NULL");
        }
    }
}
