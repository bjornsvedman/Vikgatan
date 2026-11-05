using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Documents;

namespace InvoiceManager.Api.Services;

internal static class ReceiptExpenseReviewRules
{
    public static List<string> Validate(
        IReadOnlyList<ReceiptExpenseRequest> expenses, bool approve, decimal? total, string? currency)
    {
        var errors = new List<string>();
        if (expenses.Any(expense => expense is null)) return ["Expense splits cannot contain null entries."];
        if (expenses.Count > 100) errors.Add("At most 100 expense portions are supported.");
        if (expenses.Where(item => item.Id.HasValue).GroupBy(item => item.Id).Any(group => group.Count() > 1))
            errors.Add("An expense portion ID cannot be repeated.");
        foreach (var expense in expenses)
        {
            try
            {
                ImprovementService.ValidateDraft(new(Guid.NewGuid(), expense.Description, expense.ExpenseDate,
                    expense.Type, expense.Amount, expense.Currency, expense.Notes));
            }
            catch (ArgumentException exception) { errors.Add(exception.Message); }
            if (expense.WorkDetails?.Length > 4000 || expense.ExclusionReason?.Length > 1000)
                errors.Add("Work details must be at most 4000 characters and exclusion reasons at most 1000.");
            if (!ValidAdjustment(expense.RotAmount) || !ValidAdjustment(expense.OtherCompensationAmount))
                errors.Add("ROT and other compensation must be non-negative amounts with at most four decimal places.");
            if (!approve && !expense.Confirm) continue;
            if (expense.Type == ImprovementType.Unclassified || expense.Amount is null
                || string.IsNullOrWhiteSpace(expense.Currency) || expense.ExpenseDate is null)
                errors.Add("Each confirmed expense portion needs a classification, work/expense date, amount and currency.");
            if (expense.Type == ImprovementType.Excluded)
            {
                if (string.IsNullOrWhiteSpace(expense.ExclusionReason))
                    errors.Add("Excluded expense portions need an exclusion reason.");
            }
            else if (string.IsNullOrWhiteSpace(expense.WorkDetails))
                errors.Add("Included expense portions need work details (use and previous/new standard).");
            if (approve && !expense.Confirm)
                errors.Add("Explicitly confirm every expense portion before approval.");
        }
        if (approve)
        {
            if (!string.Equals(currency?.Trim(), "SEK", StringComparison.OrdinalIgnoreCase)
                || expenses.Any(item => !string.Equals(item.Currency?.Trim(), "SEK", StringComparison.OrdinalIgnoreCase)))
                errors.Add("This version supports SEK only. Correct a misread currency explicitly; foreign-currency amounts cannot be converted or approved.");
            if (expenses.Count == 0) errors.Add("Classify the receipt's expenses before approval.");
            if (expenses.Any(item => item.Amount is null || string.IsNullOrWhiteSpace(item.Currency)
                || !string.Equals(item.Currency.Trim(), currency?.Trim(), StringComparison.OrdinalIgnoreCase)))
                errors.Add("All expense portions must use the receipt currency; no automatic conversion is supported.");
            else if (total is null || expenses.Sum(item => item.Amount!.Value) != total)
                errors.Add("Included and excluded expense portions must add up to the receipt total before approval. Incomplete or overlapping amounts may be saved as drafts.");
        }
        return errors;
    }

    public static IReadOnlyList<string> ReportBlockers(Receipt receipt)
    {
        var reasons = new List<string>();
        if (receipt.ProcessingStatus != ReceiptProcessingStatus.Completed) reasons.Add("Receipt review is pending.");
        if (receipt.ImprovementExpenses.Count == 0) reasons.Add("Expense classification is missing.");
        if (receipt.ImprovementExpenses.Any(expense => expense.ConfirmedAtUtc is null
            || expense.ConfirmedReceiptVersion != receipt.ReviewVersion))
            reasons.Add("Expense confirmation is missing or outdated.");
        var requests = receipt.ImprovementExpenses.Select(expense => new ReceiptExpenseRequest(
            expense.Id, expense.Description, expense.ExpenseDate, expense.Type, expense.Amount, expense.Currency,
            expense.Notes, expense.WorkDetails, expense.ExclusionReason, expense.RotAmount,
            expense.OtherCompensationAmount, expense.AdjustmentsAlreadyInAmount, true)).ToArray();
        reasons.AddRange(Validate(requests, true, receipt.TotalAmount, receipt.Currency));
        return reasons.Distinct().ToArray();
    }

    private static bool ValidAdjustment(decimal? amount) => amount is null
        || (amount >= 0 && amount < 100000000000000m && decimal.Round(amount.Value, 4) == amount);

    public static void Invalidate(Receipt receipt)
    {
        foreach (var expense in receipt.ImprovementExpenses)
        {
            expense.ConfirmedAtUtc = null;
            expense.ConfirmedReceiptVersion = null;
        }
    }
}
