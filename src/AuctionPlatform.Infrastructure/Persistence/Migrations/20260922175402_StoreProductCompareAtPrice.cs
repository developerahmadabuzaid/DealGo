using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreProductCompareAtPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CompareAtPrice",
                table: "StoreProducts",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreProducts_Status_CompareAtPrice",
                table: "StoreProducts",
                columns: new[] { "Status", "CompareAtPrice" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoreProducts_Status_CompareAtPrice",
                table: "StoreProducts");

            migrationBuilder.DropColumn(
                name: "CompareAtPrice",
                table: "StoreProducts");
        }
    }
}
