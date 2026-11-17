using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed record ProjectResponse(Guid Id, string Name);
public sealed record ProjectNameRequest(string? Name);

public sealed class ProjectService(InvoiceDbContext db)
{
    public async Task<IReadOnlyList<ProjectResponse>?> ListAsync(Guid propertyId, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(property => property.Id == propertyId, ct)) return null;
        return await db.Projects.AsNoTracking().Where(project => project.PropertyId == propertyId)
            .OrderBy(project => project.Name).Select(project => new ProjectResponse(project.Id, project.Name)).ToListAsync(ct);
    }

    public async Task<ProjectResponse?> SaveAsync(Guid propertyId, Guid? id, string? name, CancellationToken ct)
    {
        if (propertyId == Guid.Empty || id == Guid.Empty) throw new ArgumentException("Non-empty property and project IDs are required.");
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 128)
            throw new ArgumentException("Project name must contain 1 to 128 characters.");
        if (!await db.Properties.AnyAsync(property => property.Id == propertyId, ct)) return null;
        var project = id is Guid projectId
            ? await db.Projects.SingleOrDefaultAsync(project => project.PropertyId == propertyId && project.Id == projectId, ct)
            : new Project { PropertyId = propertyId };
        if (project is null) return null;
        project.Name = name;
        project.NormalizedName = name.ToUpperInvariant();
        project.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (id is null) db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return new(project.Id, project.Name);
    }
}

internal static class ProjectScope
{
    public static IReadOnlyList<string> Names(Receipt receipt) => receipt.LineItems.Count == 0 ? ["Unassigned"]
        : receipt.LineItems.Select(item => item.Project?.Name ?? "Unassigned").Distinct().Order().ToArray();

    public static async Task ValidateAsync(InvoiceDbContext db, Guid propertyId, Guid? projectId, bool unassigned, CancellationToken ct)
    {
        if (propertyId == Guid.Empty || projectId == Guid.Empty || (projectId is not null && unassigned))
            throw new ArgumentException("Select one valid project or Unassigned within the selected property.");
        if (projectId is Guid id && !await db.Projects.AnyAsync(project => project.Id == id && project.PropertyId == propertyId, ct))
            throw new ArgumentException("The selected project does not belong to this property.");
    }

    public static IQueryable<Receipt> Apply(IQueryable<Receipt> receipts, Guid? projectId, bool unassigned) =>
        projectId is Guid id ? receipts.Where(receipt => receipt.LineItems.Any(item => item.ProjectId == id))
        : unassigned ? receipts.Where(receipt => !receipt.LineItems.Any() || receipt.LineItems.Any(item => item.ProjectId == null))
        : receipts;
}
