using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentationCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TitleFa = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TitleEn = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IconName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentationCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentationCategories_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductDownloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    TitleFa = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TitleEn = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    NoteFa = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NoteEn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Checksum = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductDownloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductDownloads_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    TitleFa = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TitleEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MarkupFa = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    MarkupEn = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    BodyHtmlFa = table.Column<string>(type: "character varying(80000)", maxLength: 80000, nullable: true),
                    BodyHtmlEn = table.Column<string>(type: "character varying(80000)", maxLength: 80000, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductSections_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentationArticles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TitleFa = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TitleEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SummaryFa = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    SummaryEn = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    MarkupFa = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    MarkupEn = table.Column<string>(type: "character varying(40000)", maxLength: 40000, nullable: true),
                    BodyHtmlFa = table.Column<string>(type: "character varying(160000)", maxLength: 160000, nullable: true),
                    BodyHtmlEn = table.Column<string>(type: "character varying(160000)", maxLength: 160000, nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentationArticles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentationArticles_DocumentationCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "DocumentationCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DocumentationArticles_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentationSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepNumber = table.Column<int>(type: "integer", nullable: false),
                    TitleFa = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TitleEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BodyFa = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    BodyEn = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    MediaPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentationSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentationSteps_DocumentationArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "DocumentationArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationArticles_CategoryId",
                table: "DocumentationArticles",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationArticles_ProductId_IsPublished_DisplayOrder",
                table: "DocumentationArticles",
                columns: new[] { "ProductId", "IsPublished", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationArticles_ProductId_Slug",
                table: "DocumentationArticles",
                columns: new[] { "ProductId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationCategories_ProductId_Slug",
                table: "DocumentationCategories",
                columns: new[] { "ProductId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentationSteps_ArticleId_StepNumber",
                table: "DocumentationSteps",
                columns: new[] { "ArticleId", "StepNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductDownloads_ProductId_Platform_DisplayOrder",
                table: "ProductDownloads",
                columns: new[] { "ProductId", "Platform", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductSections_ProductId_DisplayOrder",
                table: "ProductSections",
                columns: new[] { "ProductId", "DisplayOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentationSteps");

            migrationBuilder.DropTable(
                name: "ProductDownloads");

            migrationBuilder.DropTable(
                name: "ProductSections");

            migrationBuilder.DropTable(
                name: "DocumentationArticles");

            migrationBuilder.DropTable(
                name: "DocumentationCategories");
        }
    }
}
