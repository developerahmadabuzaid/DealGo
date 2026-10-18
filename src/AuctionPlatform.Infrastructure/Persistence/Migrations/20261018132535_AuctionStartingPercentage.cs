using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuctionStartingPercentage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StartingPercentage",
                table: "Auctions",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 10m);

            // Existing auctions keep the starting price they already have, now written as the
            // share of their target price it amounts to.
            migrationBuilder.Sql(
                "UPDATE \"Auctions\" SET \"StartingPercentage\" = ROUND(\"StartingPrice\" * 100 / \"TargetPrice\", 2) " +
                "WHERE \"TargetPrice\" > 0 AND \"StartingPrice\" > 0 AND \"StartingPrice\" < \"TargetPrice\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartingPercentage",
                table: "Auctions");
        }
    }
}
