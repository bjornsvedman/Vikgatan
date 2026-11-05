using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.EntityFrameworkCore;

namespace InvoiceManager.Api.Services;

public sealed class PropertyService(InvoiceDbContext dbContext)
{
    public async Task<IReadOnlyList<PropertyResponse>> GetAllAsync(CancellationToken cancellationToken) =>
        await dbContext.Properties
            .AsNoTracking()
            .OrderByDescending(property => property.IsDefault)
            .ThenBy(property => property.Name)
            .Select(property => new PropertyResponse(property.Id, property.Name, property.IsDefault))
            .ToListAsync(cancellationToken);

    public async Task<PropertyResponse?> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 128)
        {
            return null;
        }

        var property = new Property
        {
            Name = normalizedName,
            IsDefault = !await dbContext.Properties.AnyAsync(cancellationToken)
        };
        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new(property.Id, property.Name, property.IsDefault);
    }

    public async Task<PropertyResponse?> SetDefaultAsync(Guid id, CancellationToken cancellationToken)
    {
        var property = await dbContext.Properties.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (property is null)
        {
            return null;
        }
        if (property.IsDefault)
        {
            return new(property.Id, property.Name, true);
        }

        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var currentDefault = await dbContext.Properties.SingleOrDefaultAsync(item => item.IsDefault, cancellationToken);
        if (currentDefault is not null)
        {
            currentDefault.IsDefault = false;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        property.IsDefault = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new(property.Id, property.Name, true);
    }
}
