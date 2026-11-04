namespace InvoiceManager.Api.Services;

public interface IReceiptAnswerProvider
{
    Task<ReceiptAnswerDraft> AnswerAsync(
        string question,
        IReadOnlyList<ReceiptQuestionDocument> documents,
        CancellationToken cancellationToken);
}