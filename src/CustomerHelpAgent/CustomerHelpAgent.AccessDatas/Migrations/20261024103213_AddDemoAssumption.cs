using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoAssumption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DemoAssumption",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    QueueWaitMinutesTraditional = table.Column<double>(type: "REAL", nullable: false),
                    QueueWaitMinutesAi = table.Column<double>(type: "REAL", nullable: false),
                    ContactsPerCaseTraditional = table.Column<double>(type: "REAL", nullable: false),
                    ContactsPerCaseAi = table.Column<double>(type: "REAL", nullable: false),
                    MinutesPerContact = table.Column<double>(type: "REAL", nullable: false),
                    StaffCostPerMinuteTwd = table.Column<double>(type: "REAL", nullable: false),
                    MonthlyCaseVolume = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoAssumption", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemoAssumption");
        }
    }
}
