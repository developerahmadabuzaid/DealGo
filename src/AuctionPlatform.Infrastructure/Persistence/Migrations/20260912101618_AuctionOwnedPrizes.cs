using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuctionOwnedPrizes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "MainProductId",
                table: "Auctions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "MainImageSnapshot",
                table: "Auctions",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "AuctionPrizes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "AuctionPrizes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            // Auctions used to borrow their picture from the linked catalog product at read time.
            // They now carry their own, so the existing rows are given the picture they were
            // already showing. Without this every auction created before today loses its image.
            migrationBuilder.Sql("""
                UPDATE "Auctions" AS a
                SET "MainImageSnapshot" = p."PrimaryImageUrl"
                FROM "Products" AS p
                WHERE a."MainProductId" = p."Id"
                  AND a."MainImageSnapshot" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MainImageSnapshot",
                table: "Auctions");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "AuctionPrizes");

            migrationBuilder.AlterColumn<Guid>(
                name: "MainProductId",
                table: "Auctions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "AuctionPrizes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
