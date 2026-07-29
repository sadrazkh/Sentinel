using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Turns the application catalogue into the general product catalogue.
/// <para>
/// Hand-written. EF scaffolded a drop-and-recreate of both tables, because renaming an entity
/// type looks to it like deleting one table and adding an unrelated one — which would have
/// destroyed every product and every entitlement in the database. This performs the rename plus
/// the added columns instead, so existing rows and their grants survive untouched.
/// </para>
/// <para>
/// The old <c>IsBeta</c> flag folds into the release ladder: a published product that was
/// marked beta becomes <c>Beta</c>, and the rest map onto the nearest new stage.
/// </para>
/// </summary>
public partial class ProductModel : Migration
{
    // Old ApplicationPublishStatus: Draft = 1, ComingSoon = 2, Published = 3, Retired = 4.
    // New ProductReleaseStatus:     Draft = 0, PrivatePreview = 1, Alpha = 2, Beta = 3,
    //                               Stable = 4, Deprecated = 5, ComingSoon = 6, Archived = 7.
    private const string MapReleaseStatus = """
        UPDATE "Products" SET "ReleaseStatus" = CASE
            WHEN "ReleaseStatus" = 3 AND "IsBeta" = TRUE THEN 3
            WHEN "ReleaseStatus" = 3 THEN 4
            WHEN "ReleaseStatus" = 4 THEN 5
            WHEN "ReleaseStatus" = 2 THEN 6
            ELSE 0
        END;
        """;

    /// <summary>Launchable | HasDocumentation — what every existing entry was already used as.</summary>
    private const int LaunchableWithDocumentation = 8 | 16;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---- categories, which nothing yet depends on --------------------------------------
        migrationBuilder.CreateTable(
            name: "ProductCategories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                NameFa = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                NameEn = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                IconName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                IsVisible = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ProductCategories", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ProductCategories_Key",
            table: "ProductCategories",
            column: "Key",
            unique: true);

        // ---- rename the catalogue in place -------------------------------------------------
        migrationBuilder.DropIndex(name: "IX_PortalApplications_Key", table: "PortalApplications");
        migrationBuilder.DropIndex(
            name: "IX_PortalApplications_PublishStatus_IsEnabled_DisplayOrder",
            table: "PortalApplications");

        migrationBuilder.RenameTable(name: "PortalApplications", newName: "Products");
        migrationBuilder.RenameColumn(name: "PublishStatus", table: "Products", newName: "ReleaseStatus");

        migrationBuilder.AddColumn<Guid>(
            name: "CategoryId", table: "Products", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "Type", table: "Products", type: "integer", nullable: false, defaultValue: 2);
        migrationBuilder.AddColumn<int>(
            name: "Capabilities", table: "Products", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(
            name: "SummaryFa", table: "Products", type: "character varying(300)", maxLength: 300, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "SummaryEn", table: "Products", type: "character varying(300)", maxLength: 300, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "CoverPath", table: "Products", type: "character varying(512)", maxLength: 512, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "CurrentVersion", table: "Products", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<bool>(
            name: "IsFeatured", table: "Products", type: "boolean", nullable: false, defaultValue: false);

        // A launch destination is only meaningful for a launchable product.
        migrationBuilder.AlterColumn<string>(
            name: "LaunchUrl", table: "Products",
            type: "character varying(2048)", maxLength: 2048, nullable: true,
            oldClrType: typeof(string), oldType: "character varying(2048)", oldMaxLength: 2048, oldNullable: false);

        migrationBuilder.Sql(MapReleaseStatus);

        // Everything that already existed was an application with a destination, so it keeps
        // exactly the capabilities it was being used with.
        migrationBuilder.Sql(
            $"""UPDATE "Products" SET "Capabilities" = {LaunchableWithDocumentation}, "Type" = 2;""");

        migrationBuilder.DropColumn(name: "IsBeta", table: "Products");

        migrationBuilder.CreateIndex(
            name: "IX_Products_Key", table: "Products", column: "Key", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_Products_ReleaseStatus_IsEnabled_DisplayOrder",
            table: "Products",
            columns: ["ReleaseStatus", "IsEnabled", "DisplayOrder"]);
        migrationBuilder.CreateIndex(
            name: "IX_Products_CategoryId", table: "Products", column: "CategoryId");

        migrationBuilder.AddForeignKey(
            name: "FK_Products_ProductCategories_CategoryId",
            table: "Products",
            column: "CategoryId",
            principalTable: "ProductCategories",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        // ---- rename the grants in place ----------------------------------------------------
        migrationBuilder.DropForeignKey(
            name: "FK_UserEntitlements_PortalApplications_ApplicationId", table: "UserEntitlements");
        migrationBuilder.DropForeignKey(
            name: "FK_UserEntitlements_Users_UserId", table: "UserEntitlements");
        migrationBuilder.DropIndex(
            name: "IX_UserEntitlements_ApplicationId", table: "UserEntitlements");
        migrationBuilder.DropIndex(
            name: "IX_UserEntitlements_UserId_ApplicationId", table: "UserEntitlements");

        migrationBuilder.RenameTable(name: "UserEntitlements", newName: "ProductEntitlements");
        migrationBuilder.RenameColumn(
            name: "ApplicationId", table: "ProductEntitlements", newName: "ProductId");

        // Everything already in the table was granted by an operator; nothing was ever bought.
        migrationBuilder.AddColumn<int>(
            name: "Source", table: "ProductEntitlements", type: "integer", nullable: false, defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "IX_ProductEntitlements_ProductId", table: "ProductEntitlements", column: "ProductId");
        migrationBuilder.CreateIndex(
            name: "IX_ProductEntitlements_UserId_ProductId",
            table: "ProductEntitlements",
            columns: ["UserId", "ProductId"],
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_ProductEntitlements_Products_ProductId",
            table: "ProductEntitlements",
            column: "ProductId",
            principalTable: "Products",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_ProductEntitlements_Users_UserId",
            table: "ProductEntitlements",
            column: "UserId",
            principalTable: "Users",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ProductEntitlements_Products_ProductId", table: "ProductEntitlements");
        migrationBuilder.DropForeignKey(
            name: "FK_ProductEntitlements_Users_UserId", table: "ProductEntitlements");
        migrationBuilder.DropIndex(
            name: "IX_ProductEntitlements_ProductId", table: "ProductEntitlements");
        migrationBuilder.DropIndex(
            name: "IX_ProductEntitlements_UserId_ProductId", table: "ProductEntitlements");
        migrationBuilder.DropColumn(name: "Source", table: "ProductEntitlements");

        migrationBuilder.RenameColumn(
            name: "ProductId", table: "ProductEntitlements", newName: "ApplicationId");
        migrationBuilder.RenameTable(name: "ProductEntitlements", newName: "UserEntitlements");

        migrationBuilder.DropForeignKey(
            name: "FK_Products_ProductCategories_CategoryId", table: "Products");
        migrationBuilder.DropIndex(name: "IX_Products_Key", table: "Products");
        migrationBuilder.DropIndex(
            name: "IX_Products_ReleaseStatus_IsEnabled_DisplayOrder", table: "Products");
        migrationBuilder.DropIndex(name: "IX_Products_CategoryId", table: "Products");

        migrationBuilder.AddColumn<bool>(
            name: "IsBeta", table: "Products", type: "boolean", nullable: false, defaultValue: false);

        // Reverse of the status mapping. Beta and Stable both came from Published, so the
        // beta flag is restored from whichever of the two a row now holds.
        migrationBuilder.Sql("""
            UPDATE "Products" SET "IsBeta" = ("ReleaseStatus" = 3);
            """);
        migrationBuilder.Sql("""
            UPDATE "Products" SET "ReleaseStatus" = CASE
                WHEN "ReleaseStatus" IN (3, 4) THEN 3
                WHEN "ReleaseStatus" = 5 THEN 4
                WHEN "ReleaseStatus" = 6 THEN 2
                ELSE 1
            END;
            """);

        migrationBuilder.Sql("""UPDATE "Products" SET "LaunchUrl" = '' WHERE "LaunchUrl" IS NULL;""");
        migrationBuilder.AlterColumn<string>(
            name: "LaunchUrl", table: "Products",
            type: "character varying(2048)", maxLength: 2048, nullable: false,
            oldClrType: typeof(string), oldType: "character varying(2048)", oldMaxLength: 2048, oldNullable: true);

        migrationBuilder.DropColumn(name: "IsFeatured", table: "Products");
        migrationBuilder.DropColumn(name: "CurrentVersion", table: "Products");
        migrationBuilder.DropColumn(name: "CoverPath", table: "Products");
        migrationBuilder.DropColumn(name: "SummaryEn", table: "Products");
        migrationBuilder.DropColumn(name: "SummaryFa", table: "Products");
        migrationBuilder.DropColumn(name: "Capabilities", table: "Products");
        migrationBuilder.DropColumn(name: "Type", table: "Products");
        migrationBuilder.DropColumn(name: "CategoryId", table: "Products");

        migrationBuilder.RenameColumn(name: "ReleaseStatus", table: "Products", newName: "PublishStatus");
        migrationBuilder.RenameTable(name: "Products", newName: "PortalApplications");

        migrationBuilder.CreateIndex(
            name: "IX_PortalApplications_Key", table: "PortalApplications", column: "Key", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_PortalApplications_PublishStatus_IsEnabled_DisplayOrder",
            table: "PortalApplications",
            columns: ["PublishStatus", "IsEnabled", "DisplayOrder"]);

        migrationBuilder.CreateIndex(
            name: "IX_UserEntitlements_ApplicationId", table: "UserEntitlements", column: "ApplicationId");
        migrationBuilder.CreateIndex(
            name: "IX_UserEntitlements_UserId_ApplicationId",
            table: "UserEntitlements",
            columns: ["UserId", "ApplicationId"],
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_UserEntitlements_PortalApplications_ApplicationId",
            table: "UserEntitlements",
            column: "ApplicationId",
            principalTable: "PortalApplications",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_UserEntitlements_Users_UserId",
            table: "UserEntitlements",
            column: "UserId",
            principalTable: "Users",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        migrationBuilder.DropTable(name: "ProductCategories");
    }
}
