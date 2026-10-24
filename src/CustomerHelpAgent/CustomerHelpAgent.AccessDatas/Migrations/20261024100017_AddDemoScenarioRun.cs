using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoScenarioRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemoRunId",
                table: "ProcessMetric",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActiveRunId",
                table: "DemoControlState",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DemoScenarioRun",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScenarioCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Family = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Flow = table.Column<int>(type: "INTEGER", nullable: false),
                    Account = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsReplay = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoScenarioRun", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessMetric_DemoRunId",
                table: "ProcessMetric",
                column: "DemoRunId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoScenarioRun_StartedAt",
                table: "DemoScenarioRun",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemoScenarioRun");

            migrationBuilder.DropIndex(
                name: "IX_ProcessMetric_DemoRunId",
                table: "ProcessMetric");

            migrationBuilder.DropColumn(
                name: "DemoRunId",
                table: "ProcessMetric");

            migrationBuilder.DropColumn(
                name: "ActiveRunId",
                table: "DemoControlState");
        }
    }
}
