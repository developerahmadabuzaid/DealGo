using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeaturedAuctionOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Auctions_IsFeatured",
                table: "Auctions");

            migrationBuilder.AddColumn<int>(
                name: "FeaturedOrder",
                table: "Auctions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_IsFeatured_FeaturedOrder",
                table: "Auctions",
                columns: new[] { "IsFeatured", "FeaturedOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Auctions_IsFeatured_FeaturedOrder",
                table: "Auctions");

            migrationBuilder.DropColumn(
                name: "FeaturedOrder",
                table: "Auctions");

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_IsFeatured",
                table: "Auctions",
                column: "IsFeatured");
        }
    }
}
