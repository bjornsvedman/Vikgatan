using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReceiptCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Receipts"
                SET "ExtractedMetadataJson" =
                    CASE
                        WHEN jsonb_typeof("ExtractedMetadataJson"->'line_items') = 'array' THEN
                            jsonb_set(
                                "ExtractedMetadataJson" - 'category',
                                '{line_items}',
                                COALESCE(
                                    (SELECT jsonb_agg(item - 'category')
                                     FROM jsonb_array_elements(
                                        CASE
                                            WHEN jsonb_typeof("ExtractedMetadataJson"->'line_items') = 'array'
                                            THEN "ExtractedMetadataJson"->'line_items'
                                            ELSE '[]'::jsonb
                                        END) AS line_items(item)),
                                    '[]'::jsonb),
                                true)
                        ELSE "ExtractedMetadataJson" - 'category'
                    END
                WHERE "ExtractedMetadataJson" IS NOT NULL
                  AND ("ExtractedMetadataJson" ? 'category'
                    OR (jsonb_typeof("ExtractedMetadataJson"->'line_items') = 'array'
                        AND EXISTS (
                            SELECT 1
                            FROM jsonb_array_elements(
                                CASE
                                    WHEN jsonb_typeof("ExtractedMetadataJson"->'line_items') = 'array'
                                    THEN "ExtractedMetadataJson"->'line_items'
                                    ELSE '[]'::jsonb
                                END) AS line_items(item)
                            WHERE item ? 'category')));
                """);

            migrationBuilder.DropIndex(
                name: "IX_Receipts_Category",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ReceiptLineItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Receipts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ReceiptLineItems",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_Category",
                table: "Receipts",
                column: "Category");
        }
    }
}
