using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M5aznDropshipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "M5aznProductId",
                table: "StoreProducts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "M5aznSku",
                table: "StoreProducts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "M5aznProductId",
                table: "AuctionPrizes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "M5aznSku",
                table: "AuctionPrizes",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "M5aznShipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'100001', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RemoteOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RemoteStatus = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrackingUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Carrier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_M5aznShipments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_M5aznProductId",
                table: "StoreProducts",
                column: "M5aznProductId",
                filter: "\"M5aznProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionPrizes_M5aznProductId",
                table: "AuctionPrizes",
                column: "M5aznProductId",
                filter: "\"M5aznProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_M5aznShipments_Reference",
                table: "M5aznShipments",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_M5aznShipments_SourceType_SourceId",
                table: "M5aznShipments",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_M5aznShipments_Status_NextAttemptAt",
                table: "M5aznShipments",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_M5aznShipments_UserId",
                table: "M5aznShipments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "M5aznShipments");

            migrationBuilder.DropIndex(
                name: "IX_StoreProducts_M5aznProductId",
                table: "StoreProducts");

            migrationBuilder.DropIndex(
                name: "IX_AuctionPrizes_M5aznProductId",
                table: "AuctionPrizes");

            migrationBuilder.DropColumn(
                name: "M5aznProductId",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "M5aznSku",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "M5aznProductId",
                table: "AuctionPrizes");

            migrationBuilder.DropColumn(
                name: "M5aznSku",
                table: "AuctionPrizes");
        }
    }
}
