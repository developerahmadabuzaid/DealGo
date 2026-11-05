using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Area",
                table: "ShippingAddresses",
                newName: "District");

            migrationBuilder.RenameColumn(
                name: "AddressLine2",
                table: "ShippingAddresses",
                newName: "Notes");

            migrationBuilder.RenameColumn(
                name: "AddressLine1",
                table: "ShippingAddresses",
                newName: "Street");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "ShippingAddresses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildingNumber",
                table: "ShippingAddresses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "ShippingAddresses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "ShippingAddresses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShortAddress",
                table: "ShippingAddresses",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "ShippingAddresses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShippingAddresses_UserId_ArchivedAt",
                table: "ShippingAddresses",
                columns: new[] { "UserId", "ArchivedAt" });

            // Until now nothing enforced this link, so clear any claim pointing at a missing address
            // before the foreign key goes on.
            migrationBuilder.Sql("""
                UPDATE "PrizeClaims" SET "ShippingAddressId" = NULL
                WHERE "ShippingAddressId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "ShippingAddresses" a WHERE a."Id" = "PrizeClaims"."ShippingAddressId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PrizeClaims_ShippingAddressId",
                table: "PrizeClaims",
                column: "ShippingAddressId");

            migrationBuilder.AddForeignKey(
                name: "FK_PrizeClaims_ShippingAddresses_ShippingAddressId",
                table: "PrizeClaims",
                column: "ShippingAddressId",
                principalTable: "ShippingAddresses",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrizeClaims_ShippingAddresses_ShippingAddressId",
                table: "PrizeClaims");

            migrationBuilder.DropIndex(
                name: "IX_ShippingAddresses_UserId_ArchivedAt",
                table: "ShippingAddresses");

            migrationBuilder.DropIndex(
                name: "IX_PrizeClaims_ShippingAddressId",
                table: "PrizeClaims");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "ShippingAddresses");

            migrationBuilder.DropColumn(
                name: "BuildingNumber",
                table: "ShippingAddresses");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "ShippingAddresses");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "ShippingAddresses");

            migrationBuilder.DropColumn(
                name: "ShortAddress",
                table: "ShippingAddresses");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ShippingAddresses");

            migrationBuilder.RenameColumn(
                name: "Street",
                table: "ShippingAddresses",
                newName: "AddressLine1");

            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "ShippingAddresses",
                newName: "AddressLine2");

            migrationBuilder.RenameColumn(
                name: "District",
                table: "ShippingAddresses",
                newName: "Area");
        }
    }
}
