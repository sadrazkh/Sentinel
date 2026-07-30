using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanNameFa = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PlanNameEn = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    PanelClientEmail = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TrafficBytes = table.Column<long>(type: "bigint", nullable: false),
                    DeviceLimit = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastUsageSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOnlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveryTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeliveryTokenIssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerServices_VpnServers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "VpnServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProvisioningJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TargetServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisioningJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvisioningJobs_CustomerServices_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "CustomerServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceInboundBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    InboundId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceInboundBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceInboundBindings_CustomerServices_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "CustomerServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerServices_DeliveryTokenHash",
                table: "CustomerServices",
                column: "DeliveryTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerServices_ServerId_PanelClientEmail",
                table: "CustomerServices",
                columns: new[] { "ServerId", "PanelClientEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerServices_ServerId_Status",
                table: "CustomerServices",
                columns: new[] { "ServerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerServices_Status_ExpiresAt",
                table: "CustomerServices",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerServices_UserId_ProductId",
                table: "CustomerServices",
                columns: new[] { "UserId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerServices_UserId_Status",
                table: "CustomerServices",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningJobs_ServiceId",
                table: "ProvisioningJobs",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvisioningJobs_Status_NextAttemptAt",
                table: "ProvisioningJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceInboundBindings_ServerId_State",
                table: "ServiceInboundBindings",
                columns: new[] { "ServerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceInboundBindings_ServiceId_ServerId_InboundId",
                table: "ServiceInboundBindings",
                columns: new[] { "ServiceId", "ServerId", "InboundId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisioningJobs");

            migrationBuilder.DropTable(
                name: "ServiceInboundBindings");

            migrationBuilder.DropTable(
                name: "CustomerServices");
        }
    }
}
