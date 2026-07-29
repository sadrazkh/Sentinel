using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpiryNoticeStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastNoticeAt",
                table: "SubscriptionSources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastNoticeStage",
                table: "SubscriptionSources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastNoticeAt",
                table: "Memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastNoticeStage",
                table: "Memberships",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastNoticeAt",
                table: "SubscriptionSources");

            migrationBuilder.DropColumn(
                name: "LastNoticeStage",
                table: "SubscriptionSources");

            migrationBuilder.DropColumn(
                name: "LastNoticeAt",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "LastNoticeStage",
                table: "Memberships");
        }
    }
}
