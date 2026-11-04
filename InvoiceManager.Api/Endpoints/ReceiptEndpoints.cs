using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceManager.Api.Endpoints;

public static class ReceiptEndpoints
{
    public static WebApplication MapReceiptEndpoints(this WebApplication app)
    {
        var documents = app.MapGroup("/api/documents").WithTags("Documents");

        documents.MapGet("/archive", BrowseAsync)
            .WithName("BrowseReceiptArchive")
            .WithSummary("Browse all receipts grouped by document year")
            .WithDescription("Filters all processing states and paginates with newest years first, then the selected sort field. Amount sorting uses stored amounts without currency conversion.")
            .Produces<ReceiptArchiveResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        documents.MapPost("/ingest", IngestAsync)
            .WithName("IngestReceipt")
            .WithSummary("Upload and register a receipt or invoice")
            .WithDescription("Stores a supported PDF or image, deduplicates by SHA-256, and returns it in Pending state.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ReceiptResponse>(StatusCodes.Status201Created)
            .Produces<ReceiptResponse>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .WithMetadata(new RequestSizeLimitAttribute(DocumentStorageOptions.MaximumRequestBytes));

        documents.MapGet("/{id:guid}", GetByIdAsync)
            .WithName("GetReceiptById")
            .WithSummary("Get a receipt by ID")
            .Produces<ReceiptResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        documents.MapGet("/{id:guid}/file", GetFileAsync)
            .WithName("GetReceiptFile")
            .WithSummary("View the original receipt file")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        documents.MapGet("/review-queue", GetReviewQueueAsync)
            .WithName("GetReceiptReviewQueue")
            .WithSummary("List receipts awaiting review")
            .WithDescription("Returns pending and partially reviewed receipts, oldest first.")
            .Produces<IReadOnlyList<ReceiptResponse>>(StatusCodes.Status200OK);

        documents.MapPut("/{id:guid}/review", ReviewAsync)
            .WithName("ReviewReceipt")
            .WithSummary("Save receipt metadata and review status")
            .WithDescription("Saves corrected metadata and line items, optionally marking the receipt as completed.")
            .Accepts<UpdateReceiptReviewRequest>("application/json")
            .Produces<ReceiptResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        documents.MapPost("/query", QueryAsync)
            .WithName("QueryReceipts")
            .WithSummary("Ask a question about stored receipts")
            .WithDescription("Answers from a bounded set of receipts and returns citations for source documents.")
            .Accepts<ReceiptQuestionRequest>("application/json")
            .Produces<ReceiptQuestionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<Results<Ok<ReceiptArchiveResponse>, ProblemHttpResult>> BrowseAsync(
        [AsParameters] ReceiptArchiveRequest request,
        ReceiptArchiveService archiveService,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await archiveService.BrowseAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid archive request.", detail: exception.Message);
        }
    }

    private static async Task<Results<Created<ReceiptResponse>, Conflict<ReceiptResponse>, ProblemHttpResult>> IngestAsync(
        IFormFile? file,
        IReceiptIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A file is required.",
                detail: "Submit one supported receipt as the 'file' form field.");
        }

        var result = await ingestionService.IngestAsync(file, cancellationToken);

        return result.Status switch
        {
            ReceiptIngestionStatus.Created => TypedResults.Created(
                $"/api/documents/{result.Receipt!.Id}", result.Receipt),
            ReceiptIngestionStatus.Duplicate => TypedResults.Conflict(result.Receipt!),
            ReceiptIngestionStatus.EmptyFile => TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The uploaded file is empty."),
            ReceiptIngestionStatus.FileTooLarge => TypedResults.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "The uploaded file is too large.",
                detail: $"Maximum file size is {DocumentStorageOptions.MaximumFileBytes} bytes."),
            ReceiptIngestionStatus.UnsupportedFileType => TypedResults.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "The file extension is not supported.",
                detail: "Supported extensions are .pdf, .jpg, .jpeg, .png, and .webp."),
            ReceiptIngestionStatus.InvalidFileContents => TypedResults.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "The file contents do not match the declared extension."),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<Results<Ok<ReceiptResponse>, NotFound>> GetByIdAsync(
        Guid id,
        IReceiptIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        var receipt = await ingestionService.GetByIdAsync(id, cancellationToken);
        return receipt is null ? TypedResults.NotFound() : TypedResults.Ok(receipt);
    }

    private static async Task<Results<PhysicalFileHttpResult, NotFound>> GetFileAsync(
        Guid id,
        IReceiptIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        var file = await ingestionService.GetFileAsync(id, cancellationToken);
        return file is null
            ? TypedResults.NotFound()
            : TypedResults.PhysicalFile(file.FullPath, file.ContentType, enableRangeProcessing: true);
    }

    private static async Task<Ok<IReadOnlyList<ReceiptResponse>>> GetReviewQueueAsync(
        IReceiptReviewService reviewService,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await reviewService.GetReviewQueueAsync(cancellationToken));

    private static async Task<Results<Ok<ReceiptResponse>, NotFound, ProblemHttpResult>> ReviewAsync(
        Guid id,
        UpdateReceiptReviewRequest request,
        IReceiptReviewService reviewService,
        CancellationToken cancellationToken)
    {
        var result = await reviewService.ReviewAsync(id, request, cancellationToken);

        return result.Status switch
        {
            ReceiptReviewStatus.Updated => TypedResults.Ok(result.Receipt!),
            ReceiptReviewStatus.NotFound => TypedResults.NotFound(),
            ReceiptReviewStatus.Invalid => TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Review data is invalid.",
                detail: string.Join(" ", result.ValidationErrors)),
            _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<Results<Ok<ReceiptQuestionResponse>, ProblemHttpResult>> QueryAsync(
        ReceiptQuestionRequest request,
        IReceiptQuestionService questionService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 2000)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A question between 1 and 2000 characters is required.");
        }

        var result = await questionService.QueryAsync(request.Question, cancellationToken);
        return TypedResults.Ok(result);
    }
}