using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APIArena.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class V1WebhooksSharingAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    DisplayPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ViewCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareLinks_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    AdapterPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SigningSecretCiphertext = table.Column<byte[]>(type: "varbinary(256)", maxLength: 256, nullable: false),
                    SigningSecretKeyId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SecretHint = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerificationExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastVerificationAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastVerificationError = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookTargets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    AggregateId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EventVersion = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    PlannedOrder = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResponseStatus = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveryAttempts_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveryAttempts_WebhookTargets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "WebhookTargets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_WebhookTargetId",
                table: "Runs",
                column: "WebhookTargetId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAt",
                table: "AuditEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_UserId_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxJobs_RunId",
                table: "OutboxJobs",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxJobs_Status_NextAttemptAt",
                table: "OutboxJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_RunId_RevokedAt",
                table: "ShareLinks",
                columns: new[] { "RunId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_TokenHash",
                table: "ShareLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryAttempts_RunId_PlannedOrder_AttemptNumber",
                table: "WebhookDeliveryAttempts",
                columns: new[] { "RunId", "PlannedOrder", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryAttempts_TargetId",
                table: "WebhookDeliveryAttempts",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookTargets_ProjectId_RevokedAt",
                table: "WebhookTargets",
                columns: new[] { "ProjectId", "RevokedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Runs_WebhookTargets_WebhookTargetId",
                table: "Runs",
                column: "WebhookTargetId",
                principalTable: "WebhookTargets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runs_WebhookTargets_WebhookTargetId",
                table: "Runs");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "OutboxJobs");

            migrationBuilder.DropTable(
                name: "ShareLinks");

            migrationBuilder.DropTable(
                name: "WebhookDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "WebhookTargets");

            migrationBuilder.DropIndex(
                name: "IX_Runs_WebhookTargetId",
                table: "Runs");
        }
    }
}
