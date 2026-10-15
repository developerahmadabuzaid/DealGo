using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TawkedVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderVerificationId",
                table: "OtpCodes",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderVerificationId",
                table: "OtpCodes");
        }
    }
}
