using System.Data;
using System.Globalization;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

internal sealed class ProjectSpendingService(InvoiceDbContext db)
{
    public async Task<ReceiptQuestionResponse> AnswerAsync(ReceiptQueryPlan plan, Guid propertyId, Guid? projectId,
        bool unassigned, CancellationToken ct)
    {
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var receipts = db.Receipts.AsNoTracking().Where(receipt => receipt.PropertyId == propertyId
            && receipt.ProcessingStatus == ReceiptProcessingStatus.Completed);
        if (plan.Year is int year) receipts = receipts.Where(receipt => receipt.DocumentDate != null && receipt.DocumentDate.Value.Year == year);
        if (!string.IsNullOrWhiteSpace(plan.Vendor))
        {
            var vendor = plan.Vendor.Trim().ToLowerInvariant();
            receipts = receipts.Where(receipt => receipt.Vendor != null && receipt.Vendor.ToLower() == vendor);
        }
        var scoped = ProjectScope.Apply(receipts, projectId, unassigned);
        var items = scoped.SelectMany(receipt => receipt.LineItems);
        if (projectId is Guid id) items = items.Where(item => item.ProjectId == id);
        if (unassigned) items = items.Where(item => item.ProjectId == null);
        var groups = await items.GroupBy(item => new
        {
            ProjectId = plan.GroupBy == "project" ? item.ProjectId : null,
            Label = plan.GroupBy == "project" ? item.Project != null ? item.Project.Name : "Unassigned"
                : plan.GroupBy == "vendor" ? item.Receipt.Vendor ?? "Unknown vendor"
                : "Recorded receipt-item spending",
            Year = plan.GroupBy == "year" && item.Receipt.DocumentDate != null ? (int?)item.Receipt.DocumentDate.Value.Year : null,
            Currency = item.Receipt.Currency ?? "Unknown currency"
        }).Select(group => new
        {
            group.Key.ProjectId, group.Key.Label, group.Key.Year, group.Key.Currency,
            Amount = group.Sum(item => item.LineTotal ?? 0m),
            Receipts = group.Select(item => item.ReceiptId).Distinct().Count()
        }).ToListAsync(ct);
        var evidence = await items.Include(item => item.Project).Include(item => item.Receipt)
            .OrderBy(item => item.ReceiptId).ThenBy(item => item.Id).ToListAsync(ct);
        var sources = await scoped.Where(receipt => items.Any(item => item.ReceiptId == receipt.Id))
            .Include(receipt => receipt.LineItems).ThenInclude(item => item.Project)
            .OrderBy(receipt => receipt.Id).ToListAsync(ct);
        var scope = projectId is Guid selectedId
            ? await db.Projects.Where(project => project.Id == selectedId && project.PropertyId == propertyId).Select(project => project.Name).SingleAsync(ct)
            : unassigned ? "Unassigned" : "All projects";
        string Label(string label, int? year) => plan.GroupBy == "year" ? year?.ToString(CultureInfo.InvariantCulture) ?? "Unknown year" : label;
        var totals = groups.OrderBy(group => group.Year).ThenBy(group => group.Label).ThenBy(group => group.Currency)
            .Select(group => new ReceiptCalculationTotal(Label(group.Label, group.Year), group.Currency, Amount(group.Amount), group.Receipts)
            { ProjectId = group.ProjectId, UnassignedProject = plan.GroupBy == "project" && group.ProjectId == null }).ToArray();
        var contributions = evidence.Select(item => new ReceiptCalculationItem(item.Id, item.ReceiptId, item.Receipt.FileName,
            item.Description, item.ProjectId, item.Project?.Name ?? "Unassigned",
            item.Receipt.Currency ?? "Unknown currency", item.LineTotal is decimal amount ? Amount(amount) : null,
            plan.GroupBy switch
            {
                "project" => item.Project?.Name ?? "Unassigned",
                "vendor" => item.Receipt.Vendor ?? "Unknown vendor",
                "year" => item.Receipt.DocumentDate?.Year.ToString(CultureInfo.InvariantCulture) ?? "Unknown year",
                _ => "Recorded receipt-item spending"
            })).ToArray();
        var warnings = new List<string>
        {
            "Recorded receipt-item spending only, not a complete project cost. No receipt totals, tax, shipping, discounts or unmatched amounts are automatically allocated.",
            "Year/vendor use source receipt metadata. Group receipt counts are distinct within each group and are not additive across mixed-project receipts."
        };
        var missing = evidence.Count(item => item.LineTotal == null);
        if (missing > 0) warnings.Add($"{missing} matching items have missing amounts; this total is incomplete. A zero known sum does not mean no spending.");
        var unassignedCount = sources.Sum(receipt => receipt.LineItems.Count(item => item.ProjectId == null));
        if (unassignedCount > 0) warnings.Add($"{unassignedCount} source items are Unassigned; project coverage is incomplete.");
        var noItems = await receipts.CountAsync(receipt => !receipt.LineItems.Any(), ct);
        if (noItems > 0) warnings.Add($"{noItems} reviewed receipts in the year/vendor scope have no items and cannot contribute project spending.");
        foreach (var receipt in sources)
        {
            if (receipt.LineItems.Any(item => item.LineTotal == null) || receipt.TotalAmount is null)
                warnings.Add($"Receipt {receipt.Id}: receipt/item reconciliation is unknown because amounts are missing.");
            else
            {
                var difference = receipt.TotalAmount.Value - receipt.LineItems.Sum(item => item.LineTotal!.Value);
                if (difference != 0m) warnings.Add($"Receipt {receipt.Id}: receipt total minus all item amounts is {Amount(difference)} {receipt.Currency ?? "Unknown currency"}; this difference is not allocated.");
            }
        }
        if (snapshot is not null) await snapshot.CommitAsync(ct);
        var answer = totals.Length == 0 ? "No recorded receipt items matched those filters."
            : "Recorded receipt-item spending:\n" + string.Join("\n", totals.Select(total =>
                $"{total.Label} / {total.Currency}: {total.Amount} ({total.ReceiptCount} distinct reviewed receipts)"));
        answer += $"\n{evidence.Count} matching items from {evidence.Select(item => item.ReceiptId).Distinct().Count()} distinct receipts.\n"
            + string.Join("\n", warnings);
        return new(answer, sources.Take(20).Select(receipt => new ReceiptQuestionCitation(receipt.Id, receipt.FileName,
            receipt.Vendor, receipt.DocumentDate, receipt.Currency, receipt.TotalAmount, receipt.ProcessingStatus)
        {
            SourceProjects = ProjectScope.Names(receipt),
            MatchedItems = contributions.Where(item => item.ReceiptId == receipt.Id).ToArray()
        }).ToArray(), sources.Count,
            new(plan.Year, plan.Vendor, plan.GroupBy, true, totals, [])
            { ItemBased = true, ProjectScope = scope, Items = contributions, CoverageWarnings = warnings });
    }

    private static string Amount(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);
}
