using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentConfirmation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CardJson = table.Column<string>(type: "TEXT", nullable: false),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AfterSalesRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConfirmation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRunTrace",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StopReason = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelCallCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCallCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRunTrace", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessionRecord",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastStopReason = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRunStep",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentRunTraceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    PlainText = table.Column<string>(type: "TEXT", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRunStep", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRunStep_AgentRunTrace_AgentRunTraceId",
                        column: x => x.AgentRunTraceId,
                        principalTable: "AgentRunTrace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConfirmation_SessionKey",
                table: "AgentConfirmation",
                column: "SessionKey");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunStep_AgentRunTraceId",
                table: "AgentRunStep",
                column: "AgentRunTraceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunTrace_SessionKey",
                table: "AgentRunTrace",
                column: "SessionKey");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRecord_CustomerUserId",
                table: "AgentSessionRecord",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessionRecord_SessionKey",
                table: "AgentSessionRecord",
                column: "SessionKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentConfirmation");

            migrationBuilder.DropTable(
                name: "AgentRunStep");

            migrationBuilder.DropTable(
                name: "AgentSessionRecord");

            migrationBuilder.DropTable(
                name: "AgentRunTrace");
        }
    }
}
