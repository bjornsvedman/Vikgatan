using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Receipts_ContentHash",
                table: "Receipts");

            migrationBuilder.CreateTable(
                name: "Properties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Properties", x => x.Id);
                });

            migrationBuilder.Sql(
                "INSERT INTO \"Properties\" (\"Id\", \"Name\", \"IsDefault\", \"CreatedAtUtc\") "
                + "VALUES ('7a4888e4-73d2-48f1-a9a8-07c55e2384b1', 'Main property', TRUE, CURRENT_TIMESTAMP);");

            migrationBuilder.AddColumn<Guid>(
                name: "PropertyId",
                table: "Receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"Receipts\" SET \"PropertyId\" = '7a4888e4-73d2-48f1-a9a8-07c55e2384b1' WHERE \"PropertyId\" IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "PropertyId",
                table: "Receipts",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_PropertyId_ContentHash",
                table: "Receipts",
                columns: new[] { "PropertyId", "ContentHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Properties_IsDefault",
                table: "Properties",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = TRUE");

            migrationBuilder.AddForeignKey(
                name: "FK_Receipts_Properties_PropertyId",
                table: "Receipts",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Receipts_Properties_PropertyId",
                table: "Receipts");

            migrationBuilder.DropTable(
                name: "Properties");

            migrationBuilder.DropIndex(
                name: "IX_Receipts_PropertyId_ContentHash",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "Receipts");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_ContentHash",
                table: "Receipts",
                column: "ContentHash",
                unique: true);
        }
    }
}
