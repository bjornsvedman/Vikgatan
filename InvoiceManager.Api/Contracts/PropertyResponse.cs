namespace InvoiceManager.Api.Contracts;

public sealed record PropertyResponse(Guid Id, string Name, bool IsDefault);

public sealed record CreatePropertyRequest(string Name);
