using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CustomerHelpAgent.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAfterSalesFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AfterSalesFulfillment",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AfterSalesRequestId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ReferenceNo = table.Column<string>(type: "TEXT", nullable: false),
                    ProductVariantId = table.Column<int>(type: "INTEGER", nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    RefundAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AfterSalesFulfillment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AfterSalesFulfillment_AfterSalesRequest_AfterSalesRequestId",
                        column: x => x.AfterSalesRequestId,
                        principalTable: "AfterSalesRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AfterSalesFulfillment_ProductVariant_ProductVariantId",
                        column: x => x.ProductVariantId,
                        principalTable: "ProductVariant",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AfterSalesFulfillment_AfterSalesRequestId",
                table: "AfterSalesFulfillment",
                column: "AfterSalesRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AfterSalesFulfillment_ProductVariantId",
                table: "AfterSalesFulfillment",
                column: "ProductVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_AfterSalesFulfillment_ReferenceNo",
                table: "AfterSalesFulfillment",
                column: "ReferenceNo",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AfterSalesFulfillment");
        }
    }
}
