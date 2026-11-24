using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuctionLastBidder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastBidParticipantId",
                table: "Auctions",
                type: "uuid",
                nullable: true);

            // Every auction that already has bids gets its last bidder filled in, so the
            // "never twice in a row" rule reads it from the row from the first bid after upgrade.
            migrationBuilder.Sql("""
                UPDATE "Auctions" AS a
                SET "LastBidParticipantId" = b."ParticipantId"
                FROM "Bids" AS b
                WHERE b."AuctionId" = a."Id"
                  AND b."SequenceNumber" = a."LastSequenceNumber"
                  AND a."LastSequenceNumber" > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastBidParticipantId",
                table: "Auctions");
        }
    }
}
