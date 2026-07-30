using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServiceMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServiceMigrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Step = table.Column<int>(type: "integer", nullable: false),
                    RemainingBytes = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceUsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DualActiveSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceMigrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceMigrations_CustomerServices_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "CustomerServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceMigrations_DestinationServerId_Step",
                table: "ServiceMigrations",
                columns: new[] { "DestinationServerId", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceMigrations_ServiceId",
                table: "ServiceMigrations",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceMigrations_SourceServerId_Step",
                table: "ServiceMigrations",
                columns: new[] { "SourceServerId", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceMigrations_Step_NextAttemptAt",
                table: "ServiceMigrations",
                columns: new[] { "Step", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceMigrations");
        }
    }
}
