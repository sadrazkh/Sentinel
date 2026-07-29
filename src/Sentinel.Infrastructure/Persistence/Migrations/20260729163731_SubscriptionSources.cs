using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriptionSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastFetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFetchStatus = table.Column<int>(type: "integer", nullable: false),
                    LastFetchError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastConfigCount = table.Column<int>(type: "integer", nullable: true),
                    UploadBytes = table.Column<long>(type: "bigint", nullable: true),
                    DownloadBytes = table.Column<long>(type: "bigint", nullable: true),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionSources_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionSources_ExpiresAt",
                table: "SubscriptionSources",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionSources_LastFetchStatus",
                table: "SubscriptionSources",
                column: "LastFetchStatus");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionSources_UserId_CreatedAt",
                table: "SubscriptionSources",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionSources_UserId_Url",
                table: "SubscriptionSources",
                columns: new[] { "UserId", "Url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionSources");
        }
    }
}
