using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Documents;
using System.Globalization;
using System.Text.Json;

namespace InvoiceManager.Api.Services;

internal static class ReceiptResponseMapper
{
    public static ReceiptResponse ToResponse(Receipt receipt) => new(
        receipt.Id,
        receipt.PropertyId,
        receipt.FileName,
        receipt.DocumentDate,
        receipt.Vendor,
        receipt.Currency,
        receipt.TotalAmount,
        receipt.Category,
        receipt.Description,
        receipt.ProcessingStatus,
        receipt.ProcessingError,
        receipt.CreatedAtUtc,
        receipt.UpdatedAtUtc,
        receipt.LineItems
            .Select(item => new ReceiptLineItemResponse(
                item.Id,
                item.Description,
                item.Quantity,
                item.UnitPrice,
                item.LineTotal,
                item.Category))
            .ToArray())
    {
        ReviewVersion = receipt.ReviewVersion,
        TotalAmountExact = Format(receipt.TotalAmount),
        Expenses = receipt.ImprovementExpenses.OrderBy(expense => expense.CreatedAtUtc).ThenBy(expense => expense.Id)
            .Select(ToExpenseResponse).ToArray(),
        ExpenseSuggestions = ReadSuggestions(receipt.ExtractedMetadataJson)
    };

    public static ReceiptExpenseResponse ToExpenseResponse(ImprovementExpense expense) => new(
        expense.Id, expense.Description, expense.ExpenseDate, expense.Type, Format(expense.Amount),
        expense.Currency, expense.Notes, expense.WorkDetails, expense.ExclusionReason,
        Format(expense.RotAmount), Format(expense.OtherCompensationAmount), expense.AdjustmentsAlreadyInAmount,
        expense.ConfirmedAtUtc, expense.ConfirmedReceiptVersion);

    private static string? Format(decimal? amount) => amount?.ToString("0.00##", CultureInfo.InvariantCulture);

    private static IReadOnlyList<ExtractedExpenseSuggestion> ReadSuggestions(string? json)
    {
        if (json is null) return [];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("expense_suggestions", out var suggestions)
            && suggestions.ValueKind == JsonValueKind.Array
            ? suggestions.Deserialize<ExtractedExpenseSuggestion[]>() ?? []
            : [];
    }
}