using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "AgentSessionRecord",
                type: "INTEGER",
                nullable: false,
                // 既有會話（M5 之前）一律是單一 Agent（AgentMode.Single = 1）；0 不是合法的模式。
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestJson",
                table: "AgentConfirmation",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "AgentSessionRecord");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestJson",
                table: "AgentConfirmation");
        }
    }
}
