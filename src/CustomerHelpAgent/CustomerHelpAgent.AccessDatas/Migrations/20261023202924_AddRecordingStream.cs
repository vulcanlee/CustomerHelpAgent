using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRecordedCall_AgentRecordingId_Sequence",
                table: "AgentRecordedCall");

            migrationBuilder.AddColumn<string>(
                name: "Stream",
                table: "AgentRecordedCall",
                type: "TEXT",
                nullable: false,
                defaultValue: "customer");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRecordedCall_AgentRecordingId_Stream_Sequence",
                table: "AgentRecordedCall",
                columns: new[] { "AgentRecordingId", "Stream", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRecordedCall_AgentRecordingId_Stream_Sequence",
                table: "AgentRecordedCall");

            migrationBuilder.DropColumn(
                name: "Stream",
                table: "AgentRecordedCall");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRecordedCall_AgentRecordingId_Sequence",
                table: "AgentRecordedCall",
                columns: new[] { "AgentRecordingId", "Sequence" });
        }
    }
}
