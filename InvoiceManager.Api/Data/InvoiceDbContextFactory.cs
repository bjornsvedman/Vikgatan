using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Pgvector.EntityFrameworkCore;

namespace InvoiceManager.Api.Data;

public sealed class InvoiceDbContextFactory : IDesignTimeDbContextFactory<InvoiceDbContext>
{
    public InvoiceDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<InvoiceDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? "Host=127.0.0.1;Port=5432;Database=invoice_manager;Username=invoice_manager";

        var options = new DbContextOptionsBuilder<InvoiceDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.UseVector())
            .Options;

        return new InvoiceDbContext(options);
    }
}