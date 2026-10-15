using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SiteShowcase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowcaseEnabled",
                table: "SiteContent",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseEyebrow",
                table: "SiteContent",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseSubtitle",
                table: "SiteContent",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShowcaseTitle",
                table: "SiteContent",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "SiteCards",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            /*
             * Bring an existing site up to the new defaults.
             *
             * A bool column added to a table that already has rows arrives as false, so a site
             * created before this migration would have the picture row switched off with no way
             * to tell why. Switching it on is safe: the section draws nothing at all until
             * somebody uploads a picture for it.
             */
            migrationBuilder.Sql("""
                UPDATE "SiteContent"
                SET "ShowcaseEnabled" = TRUE,
                    "ShowcaseEyebrow" = COALESCE("ShowcaseEyebrow", 'من داخل التطبيق'),
                    "ShowcaseTitle" = COALESCE("ShowcaseTitle", 'شاهد كيف تبدو التجربة'),
                    "ShowcaseSubtitle" = COALESCE(
                        "ShowcaseSubtitle", 'ثلاث لقطات من التطبيق، وتحت كل واحدة شرحها.');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowcaseEnabled",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "ShowcaseEyebrow",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "ShowcaseSubtitle",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "ShowcaseTitle",
                table: "SiteContent");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "SiteCards");
        }
    }
}
