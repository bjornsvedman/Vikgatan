using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptLineItems_Receipts_ReceiptId",
                table: "ReceiptLineItems");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptLineItems_ReceiptId",
                table: "ReceiptLineItems");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "ReceiptLineItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "ReceiptLineItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.UniqueConstraint("AK_Projects_PropertyId_Id", x => new { x.PropertyId, x.Id });
                    table.ForeignKey(
                        name: "FK_Projects_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                UPDATE "ReceiptLineItems" AS item
                SET "PropertyId" = receipt."PropertyId"
                FROM "Receipts" AS receipt
                WHERE item."ReceiptId" = receipt."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptLineItems_PropertyId_ProjectId",
                table: "ReceiptLineItems",
                columns: new[] { "PropertyId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptLineItems_PropertyId_ReceiptId",
                table: "ReceiptLineItems",
                columns: new[] { "PropertyId", "ReceiptId" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_PropertyId_NormalizedName",
                table: "Projects",
                columns: new[] { "PropertyId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptLineItems_Projects_PropertyId_ProjectId",
                table: "ReceiptLineItems",
                columns: new[] { "PropertyId", "ProjectId" },
                principalTable: "Projects",
                principalColumns: new[] { "PropertyId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptLineItems_Receipts_PropertyId_ReceiptId",
                table: "ReceiptLineItems",
                columns: new[] { "PropertyId", "ReceiptId" },
                principalTable: "Receipts",
                principalColumns: new[] { "PropertyId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptLineItems_Projects_PropertyId_ProjectId",
                table: "ReceiptLineItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptLineItems_Receipts_PropertyId_ReceiptId",
                table: "ReceiptLineItems");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptLineItems_PropertyId_ProjectId",
                table: "ReceiptLineItems");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptLineItems_PropertyId_ReceiptId",
                table: "ReceiptLineItems");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "ReceiptLineItems");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "ReceiptLineItems");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptLineItems_ReceiptId",
                table: "ReceiptLineItems",
                column: "ReceiptId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptLineItems_Receipts_ReceiptId",
                table: "ReceiptLineItems",
                column: "ReceiptId",
                principalTable: "Receipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
