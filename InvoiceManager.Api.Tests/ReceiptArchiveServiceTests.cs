using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceManager.Api.Tests;

public sealed class ReceiptArchiveServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InvoiceDbContext _dbContext = null!;
    private IDbContextTransaction _transaction = null!;
    private ReceiptArchiveService _service = null!;

    public async Task InitializeAsync()
    {
        _dbContext = new InvoiceDbContext(new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector()).Options);
        _transaction = await _dbContext.Database.BeginTransactionAsync();
        _service = new ReceiptArchiveService(_dbContext);
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _dbContext.DisposeAsync();
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task BrowseAsync_CategorySort_KeepsYearsDescendingAndIncludesCompleted(string direction)
    {
        var older = CreateReceipt(new DateOnly(2023, 5, 1), "Flooring");
        var paint = CreateReceipt(new DateOnly(2024, 4, 1), "Paint");
        var flooring = CreateReceipt(new DateOnly(2024, 3, 1), "Flooring");
        flooring.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
        var undated = CreateReceipt(null, null);
        _dbContext.AddRange(older, paint, flooring, undated);
        await _dbContext.SaveChangesAsync();

        var result = await _service.BrowseAsync(new(SortBy: "category", Direction: direction), CancellationToken.None);

        var expected = direction == "asc"
            ? new[] { flooring.Id, paint.Id, older.Id, undated.Id }
            : new[] { paint.Id, flooring.Id, older.Id, undated.Id };
        Assert.Equal(expected, result.Items.Select(receipt => receipt.Id));
        Assert.Equal(4, result.TotalCount);
        Assert.Contains(result.Items, receipt => receipt.Status == ReceiptProcessingStatus.Completed);
        Assert.Contains(result.Items, receipt => receipt.Status == ReceiptProcessingStatus.NeedsReview);
        Assert.Equal(new[] { 2024, 2023 }, result.Years);
    }

    [Fact]
    public async Task BrowseAsync_CombinedFilters_UsesDocumentDateAndKeepsFilterChoices()
    {
        var match = CreateReceipt(new DateOnly(2024, 5, 10), "Flooring");
        var otherCategory = CreateReceipt(new DateOnly(2024, 5, 10), "Paint");
        var otherDate = CreateReceipt(new DateOnly(2024, 6, 10), "Flooring");
        var otherStatus = CreateReceipt(new DateOnly(2024, 5, 10), "Flooring");
        otherStatus.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
        var otherCurrency = CreateReceipt(new DateOnly(2024, 5, 10), "Flooring");
        otherCurrency.Currency = "EUR";
        var otherVendor = CreateReceipt(new DateOnly(2024, 5, 10), "Flooring");
        otherVendor.Vendor = "Other vendor";
        _dbContext.AddRange(match, otherCategory, otherDate, otherStatus, otherCurrency, otherVendor);
        await _dbContext.SaveChangesAsync();

        var result = await _service.BrowseAsync(new(Year: 2024,
            DateFrom: new DateOnly(2024, 5, 1), DateTo: new DateOnly(2024, 5, 31),
            Category: "flooring", Vendor: "tile house", Currency: "sek", Status: "Completed"), CancellationToken.None);

        Assert.Equal(match.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.TotalCount);
        Assert.Contains("Paint", result.Categories);
        Assert.Contains("EUR", result.Currencies);
        Assert.Contains("Other vendor", result.Vendors);
    }

    [Fact]
    public async Task BrowseAsync_UndatedAndUncategorized_AreDiscoverable()
    {
        var undated = CreateReceipt(null, null);
        _dbContext.AddRange(undated, CreateReceipt(new DateOnly(2024, 5, 1), "Paint"));
        await _dbContext.SaveChangesAsync();

        var result = await _service.BrowseAsync(new(Undated: true, Uncategorized: true), CancellationToken.None);

        Assert.Equal(undated.Id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task BrowseAsync_Pagination_HasNoDuplicatesAndCanReturnAnEmptyPage()
    {
        _dbContext.AddRange(Enumerable.Range(0, 5).Select(index => CreateReceipt(new DateOnly(2024, 5, 1), "Paint")));
        await _dbContext.SaveChangesAsync();

        var first = await _service.BrowseAsync(new(PageSize: 2), CancellationToken.None);
        var second = await _service.BrowseAsync(new(Page: 2, PageSize: 2), CancellationToken.None);
        var repeated = await _service.BrowseAsync(new(Page: 2, PageSize: 2), CancellationToken.None);
        var empty = await _service.BrowseAsync(new(Page: 4, PageSize: 2), CancellationToken.None);

        Assert.Equal(5, first.TotalCount);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(receipt => receipt.Id).Intersect(second.Items.Select(receipt => receipt.Id)));
        Assert.Equal(second.Items.Select(receipt => receipt.Id), repeated.Items.Select(receipt => receipt.Id));
        Assert.Empty(empty.Items);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("vendor")]
    [InlineData("amount")]
    public async Task BrowseAsync_Ordering_RespectsDirectionWithinYear(string sortBy)
    {
        var lower = CreateReceipt(new DateOnly(2024, 1, 1), "Paint");
        lower.Vendor = "Alpha";
        lower.TotalAmount = 10;
        var higher = CreateReceipt(new DateOnly(2024, 12, 1), "Paint");
        higher.Vendor = "Zulu";
        higher.TotalAmount = 20;
        _dbContext.AddRange(lower, higher);
        await _dbContext.SaveChangesAsync();

        var ascending = await _service.BrowseAsync(new(SortBy: sortBy, Direction: "asc"), CancellationToken.None);
        var descending = await _service.BrowseAsync(new(SortBy: sortBy, Direction: "desc"), CancellationToken.None);

        Assert.Equal(lower.Id, ascending.Items[0].Id);
        Assert.Equal(higher.Id, descending.Items[0].Id);
    }

    [Fact]
    public async Task BrowseAsync_InvalidRequests_AreRejected()
    {
        ReceiptArchiveRequest[] requests = [new(Page: 0), new(PageSize: 101), new(SortBy: "sql"),
            new(Direction: "invalid"), new(Status: "unknown"), new(Year: 2024, Undated: true),
            new(Category: "Paint", Uncategorized: true),
            new(DateFrom: new DateOnly(2025, 1, 1), DateTo: new DateOnly(2024, 1, 1))];
        foreach (var request in requests)
            await Assert.ThrowsAsync<ArgumentException>(() => _service.BrowseAsync(request, CancellationToken.None));
    }

    private static Receipt CreateReceipt(DateOnly? date, string? category) => new()
    {
        FileName = "synthetic-receipt.pdf",
        FilePath = $"{Guid.NewGuid():N}.pdf",
        ContentHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
        DocumentDate = date,
        Category = category,
        Vendor = "Tile House",
        Currency = "SEK",
        TotalAmount = 100,
        ProcessingStatus = ReceiptProcessingStatus.Completed
    };
}