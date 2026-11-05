using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.PostgreSql;

namespace InvoiceManager.Api.Tests;

public sealed class PropertyServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InvoiceDbContext _dbContext = null!;
    private IDbContextTransaction _transaction = null!;
    private PropertyService _service = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;
        _dbContext = new InvoiceDbContext(options);
        _transaction = await _dbContext.Database.BeginTransactionAsync();
        _service = new PropertyService(_dbContext);
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _dbContext.DisposeAsync();
    }

    [Fact]
    public async Task CreateAndSetDefault_DoesNotReassignExistingReceipts()
    {
        var receipt = new Receipt
        {
            PropertyId = Property.DefaultPropertyId,
            FileName = "legacy-receipt.pdf",
            FilePath = $"{Guid.NewGuid():N}.pdf",
            ContentHash = Guid.NewGuid().ToString("N").PadRight(64, '0')
        };
        _dbContext.Receipts.Add(receipt);
        await _dbContext.SaveChangesAsync();

        var created = await _service.CreateAsync("  Vacation home  ", CancellationToken.None);
        var selected = await _service.SetDefaultAsync(created!.Id, CancellationToken.None);
        var savedReceipt = await _dbContext.Receipts.SingleAsync(item => item.Id == receipt.Id);

        Assert.Equal("Vacation home", created.Name);
        Assert.True(selected!.IsDefault);
        Assert.Equal(Property.DefaultPropertyId, savedReceipt.PropertyId);
        Assert.Equal(1, await _dbContext.Properties.CountAsync(property => property.IsDefault));
    }
}
