using InvoiceManager.Api.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
	throw new InvalidOperationException(
		"Set the 'ConnectionStrings:Postgres' connection string with .NET User Secrets or environment variables.");
}

builder.Services.AddDbContext<InvoiceDbContext>(options =>
	options.UseNpgsql(connectionString, postgres => postgres.UseVector()));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "Invoice Manager API", status = "running" }));

app.Run();
