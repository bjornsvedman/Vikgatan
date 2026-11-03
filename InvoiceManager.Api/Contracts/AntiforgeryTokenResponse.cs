namespace InvoiceManager.Api.Contracts;

/// <summary>Contains a request token for protecting browser form submissions.</summary>
public sealed record AntiforgeryTokenResponse(string RequestToken);