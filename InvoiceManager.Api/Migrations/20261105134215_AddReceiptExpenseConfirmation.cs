using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptExpenseConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewVersion",
                table: "Receipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AdjustmentsAlreadyInAmount",
                table: "ImprovementExpenses",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAtUtc",
                table: "ImprovementExpenses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConfirmedReceiptVersion",
                table: "ImprovementExpenses",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExclusionReason",
                table: "ImprovementExpenses",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OtherCompensationAmount",
                table: "ImprovementExpenses",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RotAmount",
                table: "ImprovementExpenses",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkDetails",
                table: "ImprovementExpenses",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewVersion",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "AdjustmentsAlreadyInAmount",
                table: "ImprovementExpenses");

            migrationBuilder.DropColumn(
                name: "ConfirmedAtUtc",
                table: "ImprovementExpenses");

            migrationBuilder.DropColumn(
                name: "ConfirmedReceiptVersion",
                table: "ImprovementExpenses");

            migrationBuilder.DropColumn(
                name: "ExclusionReason",
                table: "ImprovementExpenses");

            migrationBuilder.DropColumn(
                name: "OtherCompensationAmount",
                table: "ImprovementExpenses");

            migrationBuilder.DropColumn(
                name: "RotAmount",
                table: "ImprovementExpenses");

            migrationBuilder.DropColumn(
                name: "WorkDetails",
                table: "ImprovementExpenses");
        }
    }
}
