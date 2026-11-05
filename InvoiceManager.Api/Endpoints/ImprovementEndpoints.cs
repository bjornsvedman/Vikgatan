using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InvoiceManager.Api.Endpoints;

public static class ImprovementEndpoints
{
    public static WebApplication MapImprovementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/properties/{propertyId:guid}").WithTags("Improvements");
        group.AddEndpointFilter(async (context, next) =>
        {
            foreach (var name in new[] { "propertyId", "expenseId" })
            {
                if (context.HttpContext.Request.RouteValues[name] is string value
                    && Guid.TryParse(value, out var id) && id == Guid.Empty)
                    return Results.Problem(statusCode: 400, title: $"A non-empty {name} is required.");
            }
            try { return await next(context); }
            catch (ArgumentException exception)
            {
                return Results.Problem(statusCode: 400, title: "Invalid draft data.", detail: exception.Message);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(statusCode: 409, title: "Receipt changed.",
                    detail: "Reload receipt review before retrying. No expense changes were saved.");
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.ForeignKeyViolation,
                    ConstraintName: "FK_ImprovementExpenses_Receipts_PropertyId_ReceiptId"
                })
            {
                return Results.Problem(statusCode: 409, title: "Source receipt changed.",
                    detail: "The source receipt was removed while saving. Refresh and select an existing reviewed receipt.");
            }
        });
        group.MapGet("/sale-facts", async (Guid propertyId, ImprovementService service, CancellationToken ct) =>
        {
            var facts = await service.GetSaleFactsAsync(propertyId, ct);
            return facts is null ? Results.NotFound() : Results.Ok(facts);
        });
        group.MapPut("/sale-facts", async (Guid propertyId, SaleFactsRequest request, ImprovementService service, CancellationToken ct) =>
        {
            var facts = await service.SaveSaleFactsAsync(propertyId, request, ct);
            return facts is null ? Results.NotFound() : Results.Ok(facts);
        });
        group.MapGet("/expenses", async (Guid propertyId, ImprovementService service, CancellationToken ct) =>
        {
            var expenses = await service.ListAsync(propertyId, ct);
            return expenses is null ? Results.NotFound() : Results.Ok(expenses);
        });
        group.MapGet("/readiness", async (Guid propertyId, ImprovementService service, CancellationToken ct) =>
        {
            var readiness = await service.GetReadinessAsync(propertyId, ct);
            return readiness is null ? Results.NotFound() : Results.Ok(readiness);
        });
        group.MapGet("/expense-report", async (Guid propertyId, ExpenseReportService service, CancellationToken ct) =>
        {
            var report = await service.GenerateAsync(propertyId, ct);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });
        group.MapGet("/expense-report/download", async (Guid propertyId, ExpenseReportService service, CancellationToken ct) =>
        {
            var report = await service.GenerateAsync(propertyId, ct);
            return report is null ? Results.NotFound()
                : Results.File(System.Text.Encoding.UTF8.GetBytes(report.Html), "text/html; charset=utf-8",
                    $"expense-summary-{propertyId:N}.html");
        });
        group.MapPost("/expenses", async (Guid propertyId, DraftExpenseRequest request, ImprovementService service, CancellationToken ct) =>
        {
            var expense = await service.SaveAsync(propertyId, null, request, ct);
            return expense is null ? Results.NotFound()
                : Results.Created($"/api/properties/{propertyId}/expenses/{expense.Id}", expense);
        });
        group.MapPut("/expenses/{expenseId:guid}", async (Guid propertyId, Guid expenseId, DraftExpenseRequest request, ImprovementService service, CancellationToken ct) =>
        {
            var expense = await service.SaveAsync(propertyId, expenseId, request, ct);
            return expense is null ? Results.NotFound() : Results.Ok(expense);
        });
        group.MapDelete("/expenses/{expenseId:guid}", async (Guid propertyId, Guid expenseId, ImprovementService service, CancellationToken ct) =>
            await service.DeleteAsync(propertyId, expenseId, ct) ? Results.NoContent() : Results.NotFound());
        return app;
    }
}
