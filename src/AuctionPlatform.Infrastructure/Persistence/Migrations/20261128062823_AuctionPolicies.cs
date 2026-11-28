using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuctionPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuctionPolicyBody",
                table: "SiteContent",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuctionPolicyTitle",
                table: "SiteContent",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AuctionPolicyUpdatedAt",
                table: "SiteContent",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AuctionPolicyVersion",
                table: "SiteContent",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PolicyAcceptedAt",
                table: "AuctionParticipants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyVersionAccepted",
                table: "AuctionParticipants",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuctionPolicyBody",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "AuctionPolicyTitle",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "AuctionPolicyUpdatedAt",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "AuctionPolicyVersion",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "PolicyAcceptedAt",
                table: "AuctionParticipants");

            migrationBuilder.DropColumn(
                name: "PolicyVersionAccepted",
                table: "AuctionParticipants");
        }
    }
}
