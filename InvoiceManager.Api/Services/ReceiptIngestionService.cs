using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Data;
using InvoiceManager.Api.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InvoiceManager.Api.Services;

public sealed class ReceiptIngestionService(
    InvoiceDbContext dbContext,
    IOptions<DocumentStorageOptions> storageOptions,
    IOptions<FoundryExtractionOptions> extractionOptions,
    IReceiptExtractionProvider extractionProvider,
    IEmbeddingProvider embeddingProvider,
    IWebHostEnvironment environment,
    ILogger<ReceiptIngestionService> logger) : IReceiptIngestionService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal)
    {
        ".pdf",
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _storageRoot = Path.GetFullPath(
        storageOptions.Value.RootPath,
        environment.ContentRootPath);
    private readonly FoundryExtractionOptions _extractionOptions = extractionOptions.Value;

    public async Task<ReceiptIngestionResult> IngestAsync(
        IFormFile file,
        CancellationToken cancellationToken,
        Guid propertyId)
    {
        var property = await dbContext.Properties.SingleOrDefaultAsync(item => item.Id == propertyId, cancellationToken);
        if (property is null)
        {
            return new(ReceiptIngestionStatus.InvalidProperty, null);
        }

        if (file.Length == 0)
        {
            return new(ReceiptIngestionStatus.EmptyFile, null);
        }

        if (file.Length > DocumentStorageOptions.MaximumFileBytes)
        {
            return new(ReceiptIngestionStatus.FileTooLarge, null);
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            return new(ReceiptIngestionStatus.UnsupportedFileType, null);
        }

        if (!await HasValidSignatureAsync(file, extension, cancellationToken))
        {
            return new(ReceiptIngestionStatus.InvalidFileContents, null);
        }

        await using var hashStream = file.OpenReadStream();
        var contentHash = Convert.ToHexString(
            await SHA256.HashDataAsync(hashStream, cancellationToken));

        var existingReceipt = await dbContext.Receipts
            .AsNoTracking()
            .Include(receipt => receipt.LineItems)
            .SingleOrDefaultAsync(
                receipt => receipt.PropertyId == property.Id && receipt.ContentHash == contentHash,
                cancellationToken);

        if (existingReceipt is not null)
        {
            return Duplicate(existingReceipt);
        }

        Directory.CreateDirectory(_storageRoot);

        var id = Guid.NewGuid();
        var storedFileName = $"{id:N}{extension}";
        var storedFilePath = Path.Combine(_storageRoot, storedFileName);
        var now = DateTimeOffset.UtcNow;
        var receipt = new Receipt
        {
            Id = id,
            PropertyId = property.Id,
            FileName = NormalizeFileName(file.FileName, extension),
            FilePath = storedFileName,
            ContentHash = contentHash,
            Currency = "SEK",
            ProcessingStatus = ReceiptProcessingStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        try
        {
            await using (var output = new FileStream(
                storedFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(output, cancellationToken);
            }

            dbContext.Receipts.Add(receipt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsContentHashConflict(exception))
        {
            dbContext.Entry(receipt).State = EntityState.Detached;
            TryDeleteStoredFile(storedFilePath);

            var concurrentDuplicate = await dbContext.Receipts
                .AsNoTracking()
                .Include(item => item.LineItems)
                .SingleOrDefaultAsync(
                    item => item.PropertyId == property.Id && item.ContentHash == contentHash,
                    cancellationToken);

            if (concurrentDuplicate is null)
            {
                throw;
            }

            return Duplicate(concurrentDuplicate);
        }
        catch
        {
            TryDeleteStoredFile(storedFilePath);
            throw;
        }

        if (_extractionOptions.Enabled)
        {
            await ExtractMetadataAsync(receipt, storedFilePath, extension, cancellationToken);
        }

        return new(ReceiptIngestionStatus.Created, ReceiptResponseMapper.ToResponse(receipt));
    }

    public async Task<ReceiptResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await dbContext.Receipts
            .AsNoTracking()
            .Include(item => item.LineItems)
            .Include(item => item.ImprovementExpenses)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        return receipt is null ? null : ReceiptResponseMapper.ToResponse(receipt);
    }

    private async Task ExtractMetadataAsync(
        Receipt receipt,
        string storedFilePath,
        string extension,
        CancellationToken cancellationToken)
    {
        receipt.ProcessingStatus = ReceiptProcessingStatus.Processing;
        receipt.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await using var fileStream = new FileStream(
                storedFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var extraction = await extractionProvider.ExtractAsync(
                fileStream,
                receipt.FileName,
                GetContentType(extension),
                cancellationToken);

            ApplyExtraction(receipt, extraction);
            var embeddingInput = BuildEmbeddingInput(receipt);
            if (!string.IsNullOrWhiteSpace(embeddingInput))
            {
                var embedding = await embeddingProvider.GenerateAsync(embeddingInput, cancellationToken);
                if (embedding.Length != embeddingProvider.Dimensions
                    || embedding.Length != FoundryEmbeddingOptions.DefaultDimensions)
                {
                    throw new InvalidOperationException(
                        $"Embedding length {embedding.Length} does not match the configured provider and vector column dimensions.");
                }

                receipt.Embedding = new Pgvector.Vector(embedding);
                receipt.EmbeddingModel = embeddingProvider.ModelName;
            }

            receipt.ProcessingStatus = ReceiptProcessingStatus.NeedsReview;
            receipt.ProcessingError = null;
        }
        catch (OperationCanceledException)
        {
            receipt.ProcessingStatus = ReceiptProcessingStatus.Pending;
            receipt.ProcessingError = null;
            receipt.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Receipt extraction failed for receipt {ReceiptId}.", receipt.Id);
            receipt.ProcessingStatus = ReceiptProcessingStatus.Failed;
            receipt.ProcessingError = "Automatic extraction failed. Review the document and enter its details manually.";
        }

        receipt.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ApplyExtraction(Receipt receipt, ReceiptExtractionResult extraction)
    {
        var metadata = extraction.Metadata;
        receipt.Vendor = ValidText(metadata.Vendor, 256);
        receipt.DocumentDate = DateOnly.TryParseExact(
            metadata.DocumentDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var documentDate)
            ? documentDate
            : null;
        receipt.Currency = string.IsNullOrWhiteSpace(metadata.Currency) ? "SEK"
            : metadata.Currency is { Length: 3 } currency
            && currency.All(character => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            ? currency.ToUpperInvariant()
            : null;
        receipt.TotalAmount = IsValidAmount(metadata.TotalAmount) ? metadata.TotalAmount : null;
        receipt.Category = ValidText(metadata.Category, 128);
        receipt.Description = ValidText(metadata.Description, 4000);
        receipt.ExtractedMetadataJson = extraction.RawJson;
        receipt.LineItems.Clear();

        foreach (var item in (metadata.LineItems ?? []).Take(100))
        {
            var description = ValidText(item.Description, 1000);
            if (description is null)
            {
                continue;
            }

            dbContext.ReceiptLineItems.Add(new ReceiptLineItem
            {
                ReceiptId = receipt.Id,
                Description = description,
                Quantity = IsValidAmount(item.Quantity) ? item.Quantity : null,
                UnitPrice = IsValidAmount(item.UnitPrice) ? item.UnitPrice : null,
                LineTotal = IsValidAmount(item.LineTotal) ? item.LineTotal : null,
                Category = ValidText(item.Category, 128)
            });
        }
    }

    private static string? ValidText(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength
            ? null
            : normalized;
    }

    private static bool IsValidAmount(decimal? value) =>
        value is >= 0 and <= 99999999999999.9999m
        && decimal.Round(value.Value, 4) == value.Value;

    private static string GetContentType(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static string BuildEmbeddingInput(Receipt receipt)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(receipt.Vendor))
        {
            sections.Add($"Vendor: {receipt.Vendor}");
        }

        if (receipt.DocumentDate is not null)
        {
            sections.Add($"Document date: {receipt.DocumentDate:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(receipt.Category))
        {
            sections.Add($"Category: {receipt.Category}");
        }

        if (!string.IsNullOrWhiteSpace(receipt.Description))
        {
            sections.Add(receipt.Description);
        }

        if (receipt.LineItems.Count > 0)
        {
            sections.Add("Items: " + string.Join("; ", receipt.LineItems.Select(item => item.Description)));
        }

        return string.Join("\n", sections);
    }

    public async Task<StoredReceiptFile?> GetFileAsync(Guid id, CancellationToken cancellationToken)
    {
        var storedFileName = await dbContext.Receipts
            .AsNoTracking()
            .Where(receipt => receipt.Id == id)
            .Select(receipt => receipt.FilePath)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(storedFileName)
            || !string.Equals(Path.GetFileName(storedFileName), storedFileName, StringComparison.Ordinal))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(_storageRoot, storedFileName));
        var storagePrefix = _storageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _storageRoot
            : _storageRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(storagePrefix, pathComparison) || !File.Exists(fullPath))
        {
            return null;
        }

        var contentType = Path.GetExtension(storedFileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        return new StoredReceiptFile(fullPath, contentType);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await dbContext.Receipts
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (receipt is null)
        {
            return false;
        }

        if (await dbContext.ImprovementExpenses.AnyAsync(expense => expense.ReceiptId == id, cancellationToken))
        {
            throw new ReceiptHasDraftExpensesException();
        }

        var storedFileName = receipt.FilePath;
        string? stagedFilePath = null;
        if (TryGetStoredFilePath(storedFileName, out var storedFilePath) && File.Exists(storedFilePath))
        {
            stagedFilePath = Path.Combine(_storageRoot, $"{Guid.NewGuid():N}.deleting");
            File.Move(storedFilePath, stagedFilePath);
        }

        try
        {
            dbContext.Receipts.Remove(receipt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception deletionException)
        {
            if (stagedFilePath is not null && File.Exists(stagedFilePath))
            {
                try
                {
                    File.Move(stagedFilePath, Path.Combine(_storageRoot, storedFileName));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(exception, "Could not restore the receipt file after deleting receipt {ReceiptId} failed.", id);
                }
            }

            if (deletionException is DbUpdateException
                {
                    InnerException: PostgresException
                    {
                        SqlState: PostgresErrorCodes.ForeignKeyViolation,
                        ConstraintName: "FK_ImprovementExpenses_Receipts_PropertyId_ReceiptId"
                    }
                })
            {
                throw new ReceiptHasDraftExpensesException();
            }
            throw;
        }

        if (stagedFilePath is not null)
        {
            File.Delete(stagedFilePath);
        }

        return true;
    }

    private bool TryGetStoredFilePath(string storedFileName, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storedFileName)
            || !string.Equals(Path.GetFileName(storedFileName), storedFileName, StringComparison.Ordinal))
        {
            return false;
        }

        fullPath = Path.GetFullPath(Path.Combine(_storageRoot, storedFileName));
        var storagePrefix = _storageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _storageRoot
            : _storageRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(storagePrefix, pathComparison);
    }

    private static async Task<bool> HasValidSignatureAsync(
        IFormFile file,
        string extension,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[12];
        var requiredLength = extension switch
        {
            ".pdf" => 5,
            ".jpg" or ".jpeg" => 3,
            ".png" => PngSignature.Length,
            ".webp" => 12,
            _ => 0
        };

        var bytesRead = 0;
        while (bytesRead < requiredLength)
        {
            var read = await stream.ReadAsync(header.AsMemory(bytesRead, requiredLength - bytesRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead < requiredLength)
        {
            return false;
        }

        return extension switch
        {
            ".pdf" => Encoding.ASCII.GetString(header, 0, 5) == "%PDF-",
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
            ".webp" => Encoding.ASCII.GetString(header, 0, 4) == "RIFF"
                && Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            _ => false
        };
    }

    private static string NormalizeFileName(string fileName, string extension)
    {
        var normalized = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = $"receipt{extension}";
        }

        return normalized.Length <= 255 ? normalized : normalized[..255];
    }

    private static bool IsContentHashConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Receipts_PropertyId_ContentHash"
        };

    private static ReceiptIngestionResult Duplicate(Receipt receipt) =>
        new(ReceiptIngestionStatus.Duplicate, ReceiptResponseMapper.ToResponse(receipt));

    private void TryDeleteStoredFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not remove an incomplete receipt file.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Could not remove an incomplete receipt file.");
        }
    }
}