using System.Text;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace InvoiceManager.Api.Tests;

public sealed class ReceiptIngestionServiceTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        $"invoice-manager-tests-{Guid.NewGuid():N}");

    private InvoiceDbContext _dbContext = null!;
    private IDbContextTransaction _transaction = null!;
    private ReceiptIngestionService _service = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(fixture.Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;

        _dbContext = new InvoiceDbContext(options);
        _transaction = await _dbContext.Database.BeginTransactionAsync();
        _service = new ReceiptIngestionService(
            _dbContext,
            Options.Create(new DocumentStorageOptions { RootPath = _storageRoot }),
            new TestWebHostEnvironment { ContentRootPath = _storageRoot },
            NullLogger<ReceiptIngestionService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _dbContext.DisposeAsync();

        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IngestAsync_ValidPdf_CreatesPendingReceiptAndStoresFile()
    {
        var (file, stream) = CreateFile("receipt.pdf", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var result = await _service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.Created, result.Status);
            Assert.NotNull(result.Receipt);
            Assert.Equal(ReceiptProcessingStatus.Pending, result.Receipt.Status);

            var storedReceipt = await _dbContext.Receipts.SingleAsync();
            Assert.Equal(result.Receipt.Id, storedReceipt.Id);
            Assert.Equal(64, storedReceipt.ContentHash.Length);
            Assert.True(File.Exists(Path.Combine(_storageRoot, storedReceipt.FilePath)));
        }
    }

    [Fact]
    public async Task IngestAsync_SameContentTwice_ReturnsExistingReceipt()
    {
        var (firstFile, firstStream) = CreateFile("first.pdf", "%PDF-1.4\nidentical");
        var (secondFile, secondStream) = CreateFile("renamed.pdf", "%PDF-1.4\nidentical");
        using (firstStream)
        using (secondStream)
        {
            var first = await _service.IngestAsync(firstFile, CancellationToken.None);
            var second = await _service.IngestAsync(secondFile, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.Created, first.Status);
            Assert.Equal(ReceiptIngestionStatus.Duplicate, second.Status);
            Assert.Equal(first.Receipt!.Id, second.Receipt!.Id);
            Assert.Single(await _dbContext.Receipts.ToListAsync());
            Assert.Single(Directory.GetFiles(_storageRoot));
        }
    }

    [Fact]
    public async Task IngestAsync_UnsupportedExtension_RejectsWithoutWriting()
    {
        var (file, stream) = CreateFile("receipt.txt", "%PDF-1.4\nreceipt");
        using (stream)
        {
            var result = await _service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.UnsupportedFileType, result.Status);
            Assert.Empty(await _dbContext.Receipts.ToListAsync());
            Assert.False(Directory.Exists(_storageRoot));
        }
    }

    [Fact]
    public async Task IngestAsync_InvalidSignature_RejectsWithoutWriting()
    {
        var (file, stream) = CreateFile("receipt.pdf", "not a PDF");
        using (stream)
        {
            var result = await _service.IngestAsync(file, CancellationToken.None);

            Assert.Equal(ReceiptIngestionStatus.InvalidFileContents, result.Status);
            Assert.Empty(await _dbContext.Receipts.ToListAsync());
            Assert.False(Directory.Exists(_storageRoot));
        }
    }

    [Fact]
    public async Task IngestAsync_EmptyFile_RejectsWithoutWriting()
    {
        using var stream = new MemoryStream();
        var file = new FormFile(stream, 0, 0, "file", "receipt.pdf");

        var result = await _service.IngestAsync(file, CancellationToken.None);

        Assert.Equal(ReceiptIngestionStatus.EmptyFile, result.Status);
        Assert.Empty(await _dbContext.Receipts.ToListAsync());
        Assert.False(Directory.Exists(_storageRoot));
    }

    [Fact]
    public async Task IngestAsync_FileExceedsLimit_RejectsBeforeReading()
    {
        using var stream = new MemoryStream(
            new byte[checked((int)DocumentStorageOptions.MaximumFileBytes + 1)]);
        var file = new FormFile(stream, 0, stream.Length, "file", "receipt.pdf");

        var result = await _service.IngestAsync(file, CancellationToken.None);

        Assert.Equal(ReceiptIngestionStatus.FileTooLarge, result.Status);
        Assert.Empty(await _dbContext.Receipts.ToListAsync());
        Assert.False(Directory.Exists(_storageRoot));
    }

    private static (FormFile File, MemoryStream Stream) CreateFile(string fileName, string contents)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(contents));
        return (new FormFile(stream, 0, stream.Length, "file", fileName), stream);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "InvoiceManager.Api.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Testing";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("invoice_manager_tests")
        .WithUsername("invoice_manager")
        .WithPassword("test-only-password")
        .Build();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(Container.GetConnectionString(), postgres => postgres.UseVector())
            .Options;

        await using var dbContext = new InvoiceDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}