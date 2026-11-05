namespace InvoiceManager.Api.Services;

public sealed class ReceiptHasDraftExpensesException()
    : Exception("Remove this receipt's draft improvement expenses before deleting its source file.");
