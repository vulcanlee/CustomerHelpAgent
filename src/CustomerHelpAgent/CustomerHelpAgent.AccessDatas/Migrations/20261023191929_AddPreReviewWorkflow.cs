using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddPreReviewWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreReviewRun",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AfterSalesRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    CaseKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PhotoJson = table.Column<string>(type: "TEXT", nullable: true),
                    RuleJson = table.Column<string>(type: "TEXT", nullable: true),
                    HistoryJson = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    Recommendation = table.Column<string>(type: "TEXT", nullable: true),
                    HasWarning = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ReplayRecordingId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreReviewRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreReviewRun_AfterSalesRequest_AfterSalesRequestId",
                        column: x => x.AfterSalesRequestId,
                        principalTable: "AfterSalesRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowCheckpoint",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    CheckpointId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentCheckpointId = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowCheckpoint", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PreReviewNodeState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PreReviewRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    Node = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreReviewNodeState", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreReviewNodeState_PreReviewRun_PreReviewRunId",
                        column: x => x.PreReviewRunId,
                        principalTable: "PreReviewRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreReviewNodeState_PreReviewRunId_Node",
                table: "PreReviewNodeState",
                columns: new[] { "PreReviewRunId", "Node" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreReviewRun_AfterSalesRequestId_Attempt",
                table: "PreReviewRun",
                columns: new[] { "AfterSalesRequestId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreReviewRun_WorkflowSessionId",
                table: "PreReviewRun",
                column: "WorkflowSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoint_SessionId_CheckpointId",
                table: "WorkflowCheckpoint",
                columns: new[] { "SessionId", "CheckpointId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoint_SessionId_Id",
                table: "WorkflowCheckpoint",
                columns: new[] { "SessionId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreReviewNodeState");

            migrationBuilder.DropTable(
                name: "WorkflowCheckpoint");

            migrationBuilder.DropTable(
                name: "PreReviewRun");
        }
    }
}
