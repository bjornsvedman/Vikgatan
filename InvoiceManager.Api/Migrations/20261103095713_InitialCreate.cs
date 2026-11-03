using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace InvoiceManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "Receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Vendor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ExtractedMetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    ProcessingStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProcessingError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmbeddingModelVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Embedding = table.Column<Vector>(type: "vector", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptLineItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    LineTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptLineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiptLineItems_Receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptLineItems_ReceiptId",
                table: "ReceiptLineItems",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_Category",
                table: "Receipts",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_ContentHash",
                table: "Receipts",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_DocumentDate_Vendor",
                table: "Receipts",
                columns: new[] { "DocumentDate", "Vendor" });

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_ProcessingStatus",
                table: "Receipts",
                column: "ProcessingStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiptLineItems");

            migrationBuilder.DropTable(
                name: "Receipts");
        }
    }
}
