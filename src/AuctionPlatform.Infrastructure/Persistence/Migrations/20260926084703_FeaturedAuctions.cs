using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeaturedAuctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFeatured",
                table: "Auctions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_IsFeatured",
                table: "Auctions",
                column: "IsFeatured");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Auctions_IsFeatured",
                table: "Auctions");

            migrationBuilder.DropColumn(
                name: "IsFeatured",
                table: "Auctions");
        }
    }
}
