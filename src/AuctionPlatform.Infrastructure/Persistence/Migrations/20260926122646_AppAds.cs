using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppAds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppAds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Placement = table.Column<int>(type: "integer", maxLength: 32, nullable: false),
                    ImageUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetKind = table.Column<int>(type: "integer", maxLength: 32, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppAds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppAds_Placement_IsPublished_DisplayOrder",
                table: "AppAds",
                columns: new[] { "Placement", "IsPublished", "DisplayOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppAds");
        }
    }
}
