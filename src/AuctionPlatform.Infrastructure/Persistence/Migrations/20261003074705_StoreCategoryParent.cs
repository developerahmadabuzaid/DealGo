using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreCategoryParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "StoreCategories",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreCategories_ParentId",
                table: "StoreCategories",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_StoreCategories_StoreCategories_ParentId",
                table: "StoreCategories",
                column: "ParentId",
                principalTable: "StoreCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StoreCategories_StoreCategories_ParentId",
                table: "StoreCategories");

            migrationBuilder.DropIndex(
                name: "IX_StoreCategories_ParentId",
                table: "StoreCategories");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "StoreCategories");
        }
    }
}
