using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed class ExpenseReportService(InvoiceDbContext dbContext)
{
    public async Task<ExpenseReportResponse?> GenerateAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        if (propertyId == Guid.Empty) throw new ArgumentException("A non-empty propertyId is required.");
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;
        var property = await dbContext.Properties.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == propertyId, cancellationToken);
        if (property is null) return null;
        var receipts = await dbContext.Receipts.AsNoTracking().Include(receipt => receipt.ImprovementExpenses)
            .Where(receipt => receipt.PropertyId == propertyId).OrderBy(receipt => receipt.Id)
            .ToListAsync(cancellationToken);
        var eligible = new List<ImprovementExpense>();
        var unresolved = new List<UnresolvedExpenseReceipt>();
        var confirmedReceipts = 0;
        foreach (var receipt in receipts)
        {
            var reasons = ReceiptExpenseReviewRules.ReportBlockers(receipt);
            if (reasons.Count > 0)
            {
                unresolved.Add(new(receipt.Id, receipt.FileName, receipt.Vendor, receipt.Currency, reasons,
                    receipt.ImprovementExpenses.OrderBy(expense => expense.Id)
                        .Select(ReceiptResponseMapper.ToExpenseResponse).ToArray()));
                continue;
            }
            confirmedReceipts++;
            eligible.AddRange(receipt.ImprovementExpenses);
        }
        var ordered = eligible.OrderBy(expense => expense.ExpenseDate).ThenBy(expense => expense.Type)
            .ThenBy(expense => expense.ReceiptId).ThenBy(expense => expense.Id).ToArray();
        var included = ordered.Where(expense => expense.Type != ImprovementType.Excluded).ToArray();
        var excluded = ordered.Where(expense => expense.Type == ImprovementType.Excluded).ToArray();
        var totals = ordered.GroupBy(expense => new { Year = expense.ExpenseDate!.Value.Year, expense.Type })
            .OrderBy(group => group.Key.Year).ThenBy(group => group.Key.Type)
            .Select(group => new ExpenseReportTotal(group.Key.Year, Label(group.Key.Type),
                Amount(group.Sum(expense => expense.Amount!.Value)))).ToArray();
        var report = new ExpenseReportResponse(property.Id, property.Name,
            new(property.PropertyType, property.AcquisitionDate, property.SaleDate), DateTimeOffset.UtcNow,
            "expense-summary-v1", "SEK", receipts.Count, confirmedReceipts,
            included.Select(Row).ToArray(), excluded.Select(Row).ToArray(), unresolved, totals,
            Amount(included.Sum(expense => expense.Amount!.Value)),
            Amount(excluded.Sum(expense => expense.Amount!.Value)), "");
        return report with { Html = Render(report) };
    }

    private static string Amount(decimal amount) => amount.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Label(ImprovementType type) => type switch
    {
        ImprovementType.BasicImprovement => "Basic improvement",
        ImprovementType.RepairsAndMaintenance => "Repairs and maintenance",
        ImprovementType.Excluded => "Excluded / not deductible",
        _ => "Unclassified"
    };

    private static ExpenseReportRow Row(ImprovementExpense expense) => new(
        expense.ReceiptId, expense.Id, expense.Receipt.FileName, expense.Receipt.Vendor,
        expense.ExpenseDate!.Value, expense.Description, Label(expense.Type), Amount(expense.Amount!.Value),
        expense.WorkDetails, expense.ExclusionReason, expense.Notes);

    private static string Render(ExpenseReportResponse report)
    {
        static string E(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Not provided");
        static string Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Not provided";
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Expense preparation summary</title><style>
            body{font:15px system-ui,sans-serif;line-height:1.5;margin:2rem;color:#17212b}
            h1,h2,h3{break-after:avoid}table{border-collapse:collapse;width:100%;margin:1rem 0}
            th,td{border:1px solid #bbb;text-align:left;padding:.5rem;vertical-align:top;overflow-wrap:anywhere}
            .notice{border:2px solid #845900;padding:1rem}.reference{font-size:.8rem}
            @media print{body{margin:0;font-size:10pt}thead{display:table-header-group}tr{break-inside:avoid}
            @page{size:A4;margin:15mm}}
            </style></head><body><h1>Expense preparation summary</h1>
            <p class="notice"><strong>Preparation only — not a tax deduction calculation or SKV 2197.</strong>
            ROT, insurance and subsidy adjustments are not assessed or subtracted. Eligibility, thresholds,
            sale-year windows and condition/wear have not been assessed. Recorded amounts may already reflect
            adjustments on the original invoice. Do not transfer these totals directly into a tax declaration.</p>
            """);
        html.Append($"<h2>{E(report.PropertyName)}</h2><p>Generated: {E(report.GeneratedAtUtc.ToString("O"))} · {E(report.FormatVersion)} · SEK only</p>");
        html.Append($"<p>Property type: {E(report.SaleFacts.PropertyType)} · Acquired: {Date(report.SaleFacts.AcquisitionDate)} · Sold: {Date(report.SaleFacts.SaleDate)}</p>");
        html.Append($"<h2>Coverage</h2><p>{report.ConfirmedReceiptCount} of {report.ReceiptCount} receipts fully confirmed and reconciled. {report.Unresolved.Count} unresolved receipts omitted from confirmed totals.</p>");
        if (report.ConfirmedReceiptCount == 0) html.Append("<p><strong>No confirmed receipt amounts are included.</strong></p>");
        if (report.Unresolved.Count > 0) html.Append("<p><strong>Partial summary: unresolved expenses are not zero and are not included below.</strong></p>");
        html.Append($"<p>Recorded potential improvement expenses: <strong>{report.IncludedTotal} SEK</strong> (not deductible totals).<br>Separately excluded expenses: {report.ExcludedTotal} SEK.</p>");
        html.Append("<h2>Recorded amounts by expense year and classification</h2><table><thead><tr><th>Expense year</th><th>Classification</th><th>Amount (SEK)</th></tr></thead><tbody>");
        foreach (var total in report.Totals)
            html.Append($"<tr><td>{total.Year}</td><td>{E(total.Classification)}</td><td>{total.Amount}</td></tr>");
        html.Append("</tbody></table>");
        void Rows(string heading, IReadOnlyList<ExpenseReportRow> rows)
        {
            html.Append($"<h2>{heading}</h2>");
            if (rows.Count == 0) { html.Append("<p>No confirmed expenses in this section.</p>"); return; }
            foreach (var group in rows.GroupBy(row => new { row.ExpenseDate.Year, row.Classification }))
            {
                html.Append($"<h3>{group.Key.Year} — {E(group.Key.Classification)}</h3><table><thead><tr><th>Date / expense</th><th>Amount (SEK)</th><th>Source / evidence</th></tr></thead><tbody>");
                foreach (var row in group)
                    html.Append($"<tr><td>{Date(row.ExpenseDate)}<br>{E(row.Description)}<br>{E(row.WorkDetails ?? row.ExclusionReason)}<br>Notes: {E(row.Notes)}</td><td>{row.Amount}</td><td>{E(row.Vendor)}<br>{E(row.FileName)}<div class=\"reference\">Receipt: {row.ReceiptId}<br>Expense portion: {row.PortionId}</div></td></tr>");
                html.Append("</tbody></table>");
            }
        }
        Rows("Confirmed potential improvement expenses — not deductions", report.Included);
        Rows("Confirmed excluded expenses — not in improvement totals", report.Excluded);
        html.Append("<h2>Unresolved receipts — outside confirmed totals</h2>");
        if (report.Unresolved.Count == 0) html.Append("<p>No unresolved receipts. Tax eligibility remains unassessed.</p>");
        foreach (var item in report.Unresolved)
        {
            html.Append($"<h3>{E(item.Vendor ?? item.FileName)}</h3><p>{E(item.FileName)} · Receipt: {item.ReceiptId} · Stored currency: {E(item.Currency)}</p><ul>");
            foreach (var reason in item.Reasons) html.Append($"<li>{E(reason)}</li>");
            html.Append("</ul>");
            foreach (var portion in item.Portions)
                html.Append($"<p>Expense portion {portion.Id}: {E(portion.Description)} · {E(Label(portion.Type))} · {E(portion.Amount)} {E(portion.Currency)} · {Date(portion.ExpenseDate)} (not included)</p>");
        }
        html.Append("<p>Receipt and expense portion IDs identify the original local evidence. This download contains no receipt files. Open the receipt in the local archive to inspect it. Downloading again generates a fresh summary from saved data.</p></body></html>");
        return html.ToString();
    }
}
