using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreExclusiveOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsExclusiveOffer",
                table: "StoreProducts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfferEndsAt",
                table: "StoreProducts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OfferMaxPerUser",
                table: "StoreProducts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OfferStartsAt",
                table: "StoreProducts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_IsExclusiveOffer_OfferEndsAt",
                table: "StoreProducts",
                columns: new[] { "IsExclusiveOffer", "OfferEndsAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoreProducts_IsExclusiveOffer_OfferEndsAt",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "IsExclusiveOffer",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "OfferEndsAt",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "OfferMaxPerUser",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "OfferStartsAt",
                table: "StoreProducts");
        }
    }
}
