using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentRecording",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    IsGolden = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScenarioCode = table.Column<string>(type: "TEXT", nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCostTwd = table.Column<decimal>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRecording", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRecordedCall",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentRecordingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestDigest = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                    UsageJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRecordedCall", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRecordedCall_AgentRecording_AgentRecordingId",
                        column: x => x.AgentRecordingId,
                        principalTable: "AgentRecording",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentRecordedTurn",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentRecordingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerText = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRecordedTurn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRecordedTurn_AgentRecording_AgentRecordingId",
                        column: x => x.AgentRecordingId,
                        principalTable: "AgentRecording",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRecordedCall_AgentRecordingId_Sequence",
                table: "AgentRecordedCall",
                columns: new[] { "AgentRecordingId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRecordedTurn_AgentRecordingId_Sequence",
                table: "AgentRecordedTurn",
                columns: new[] { "AgentRecordingId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRecording_ScenarioCode",
                table: "AgentRecording",
                column: "ScenarioCode",
                unique: true,
                filter: "\"IsGolden\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRecording_SessionKey",
                table: "AgentRecording",
                column: "SessionKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentRecordedCall");

            migrationBuilder.DropTable(
                name: "AgentRecordedTurn");

            migrationBuilder.DropTable(
                name: "AgentRecording");
        }
    }
}
