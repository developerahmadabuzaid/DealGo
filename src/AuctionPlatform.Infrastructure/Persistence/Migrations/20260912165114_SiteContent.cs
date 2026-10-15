using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SiteContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Icon = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteCards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteContent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HeroBadge = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HeroTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HeroSubtitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    HeroCtaLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HeroCtaUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    HeroImageUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AppStoreUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    GooglePlayUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AboutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AboutEyebrow = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AboutTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FeaturesTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FeaturesBody = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    VisionTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VisionBody = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    StatUsersOverride = table.Column<int>(type: "integer", nullable: true),
                    StatAuctionsOverride = table.Column<int>(type: "integer", nullable: true),
                    StatProductsOverride = table.Column<int>(type: "integer", nullable: true),
                    StatUsersLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StatAuctionsLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StatProductsLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StepsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    StepsEyebrow = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StepsTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StepsSubtitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ParticipateEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ParticipateEyebrow = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ParticipateTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ParticipateSubtitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TestimonialsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TestimonialsEyebrow = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    TestimonialsTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TestimonialsSubtitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FaqEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FaqEyebrow = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    FaqTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FaqSubtitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DownloadEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DownloadTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DownloadSubtitle = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FooterNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PrivacyUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TermsUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TwitterUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    InstagramUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TiktokUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SnapchatUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    WhatsappUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    MetaTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MetaDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteContent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteFaqs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteFaqs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteTestimonials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Role = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteTestimonials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteCards_Kind_DisplayOrder",
                table: "SiteCards",
                columns: new[] { "Kind", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SiteFaqs_IsPublished_DisplayOrder",
                table: "SiteFaqs",
                columns: new[] { "IsPublished", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SiteTestimonials_IsPublished_DisplayOrder",
                table: "SiteTestimonials",
                columns: new[] { "IsPublished", "DisplayOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteCards");

            migrationBuilder.DropTable(
                name: "SiteContent");

            migrationBuilder.DropTable(
                name: "SiteFaqs");

            migrationBuilder.DropTable(
                name: "SiteTestimonials");
        }
    }
}
