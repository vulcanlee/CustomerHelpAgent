using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerMemory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    SourceSessionKey = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerMemory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMemory_CustomerUserId",
                table: "CustomerMemory",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMemory_CustomerUserId_Kind",
                table: "CustomerMemory",
                columns: new[] { "CustomerUserId", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerMemory");
        }
    }
}
