using InvoiceManager.Api.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InvoiceManager.Api.Endpoints;

public static class ProjectEndpoints
{
    public static WebApplication MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/properties/{propertyId:guid}/projects");
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Request.RouteValues.Values.Any(value => Guid.TryParse(value?.ToString(), out var id) && id == Guid.Empty))
                return Results.Problem(statusCode: 400, title: "Non-empty property and project IDs are required.");
            try { return await next(context); }
            catch (ArgumentException exception) { return Results.Problem(statusCode: 400, title: "Invalid project.", detail: exception.Message); }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Projects_PropertyId_NormalizedName" })
            {
                return Results.Problem(statusCode: 409, title: "Project name already exists.", detail: "Choose a different name in this property.");
            }
        });
        group.MapGet("/", async (Guid propertyId, ProjectService service, CancellationToken ct) =>
        {
            var projects = await service.ListAsync(propertyId, ct);
            return projects is null ? Results.NotFound() : Results.Ok(projects);
        });
        group.MapPost("/", async (Guid propertyId, ProjectNameRequest request, ProjectService service, CancellationToken ct) =>
        {
            var project = await service.SaveAsync(propertyId, null, request.Name, ct);
            return project is null ? Results.NotFound() : Results.Created($"/api/properties/{propertyId}/projects/{project.Id}", project);
        });
        group.MapPut("/{projectId:guid}", async (Guid propertyId, Guid projectId, ProjectNameRequest request, ProjectService service, CancellationToken ct) =>
        {
            var project = await service.SaveAsync(propertyId, projectId, request.Name, ct);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });
        return app;
    }
}
