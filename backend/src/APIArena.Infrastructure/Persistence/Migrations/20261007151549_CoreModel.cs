using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace APIArena.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoreModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CaptureBodies = table.Column<bool>(type: "bit", nullable: false),
                    SimulatorAllowedOrigins = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.CheckConstraint("CK_Projects_Name", "LEN([Name]) > 0");
                    table.ForeignKey(
                        name: "FK_Projects_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScenarioTemplates_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Engine = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EvaluatorVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioVersions", x => x.Id);
                    table.CheckConstraint("CK_ScenarioVersions_Version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_ScenarioVersions_ScenarioTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ScenarioTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScenarioVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScenarioSlug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ScenarioVersionNumber = table.Column<int>(type: "int", nullable: false),
                    Engine = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EvaluatorVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ConfigSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Seed = table.Column<int>(type: "int", nullable: false),
                    InboundLimit = table.Column<int>(type: "int", nullable: false),
                    TtlSeconds = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextSequence = table.Column<long>(type: "bigint", nullable: false),
                    InboundCount = table.Column<int>(type: "int", nullable: false),
                    QuotaExceeded = table.Column<bool>(type: "bit", nullable: false),
                    RateLimitedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IncompleteReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    WebhookTargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.CheckConstraint("CK_Runs_Counts", "[InboundCount] >= 0 AND [InboundCount] <= [InboundLimit] AND [NextSequence] >= 0");
                    table.CheckConstraint("CK_Runs_Status", "[Status] BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_Runs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Runs_ScenarioVersions_ScenarioVersionId",
                        column: x => x.ScenarioVersionId,
                        principalTable: "ScenarioVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReportSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScenarioSlug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ScenarioVersion = table.Column<int>(type: "int", nullable: false),
                    EvaluatorVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RunFinalStatus = table.Column<int>(type: "int", nullable: false),
                    IncompleteReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Passed = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Inconclusive = table.Column<int>(type: "int", nullable: false),
                    NotApplicable = table.Column<int>(type: "int", nullable: false),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    ReportJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportSnapshots_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Route = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PlannedStatus = table.Column<int>(type: "int", nullable: false),
                    PlannedDelayMs = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HeadersJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    BodyBytes = table.Column<int>(type: "int", nullable: false),
                    BodyPreview = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    BodyTruncated = table.Column<bool>(type: "bit", nullable: false),
                    RejectReason = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ResponsePreview = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestObservations_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunAccessKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    DisplayPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunAccessKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunAccessKeys_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DataJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunEvents_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunOperations",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunOperations", x => new { x.RunId, x.OperationKey });
                    table.ForeignKey(
                        name: "FK_RunOperations_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CheckResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckId = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    EvidenceKind = table.Column<int>(type: "int", nullable: false),
                    Expected = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Observed = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Suggestion = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvidenceJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    EvaluatorVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckResults_ReportSnapshots_ReportId",
                        column: x => x.ReportId,
                        principalTable: "ReportSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_ReportId_CheckId",
                table: "CheckResults",
                columns: new[] { "ReportId", "CheckId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerId_DeletedAt_CreatedAt",
                table: "Projects",
                columns: new[] { "OwnerId", "DeletedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSnapshots_RunId",
                table: "ReportSnapshots",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestObservations_RunId_Sequence",
                table: "RequestObservations",
                columns: new[] { "RunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunAccessKeys_KeyHash",
                table: "RunAccessKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunAccessKeys_RunId_RevokedAt",
                table: "RunAccessKeys",
                columns: new[] { "RunId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunEvents_RunId_Sequence",
                table: "RunEvents",
                columns: new[] { "RunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ProjectId_CreatedAt",
                table: "Runs",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ScenarioVersionId",
                table: "Runs",
                column: "ScenarioVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status_ExpiresAt",
                table: "Runs",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioTemplates_OwnerId_Slug",
                table: "ScenarioTemplates",
                columns: new[] { "OwnerId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ScenarioTemplates_BuiltInSlug",
                table: "ScenarioTemplates",
                column: "Slug",
                unique: true,
                filter: "[OwnerId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioVersions_TemplateId_Version",
                table: "ScenarioVersions",
                columns: new[] { "TemplateId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckResults");

            migrationBuilder.DropTable(
                name: "RequestObservations");

            migrationBuilder.DropTable(
                name: "RunAccessKeys");

            migrationBuilder.DropTable(
                name: "RunEvents");

            migrationBuilder.DropTable(
                name: "RunOperations");

            migrationBuilder.DropTable(
                name: "ReportSnapshots");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "ScenarioVersions");

            migrationBuilder.DropTable(
                name: "ScenarioTemplates");
        }
    }
}
