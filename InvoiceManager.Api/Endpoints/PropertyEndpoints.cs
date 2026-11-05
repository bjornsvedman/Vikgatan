using InvoiceManager.Api.Contracts;
using InvoiceManager.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace InvoiceManager.Api.Endpoints;

public static class PropertyEndpoints
{
    public static WebApplication MapPropertyEndpoints(this WebApplication app)
    {
        var properties = app.MapGroup("/api/properties").WithTags("Properties");
        properties.MapGet("/", GetAllAsync)
            .WithName("GetProperties")
            .WithSummary("List property workspaces")
            .Produces<IReadOnlyList<PropertyResponse>>();
        properties.MapPost("/", CreateAsync)
            .WithName("CreateProperty")
            .WithSummary("Create a property workspace")
            .Produces<PropertyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        properties.MapPut("/{id:guid}/default", SetDefaultAsync)
            .WithName("SetDefaultProperty")
            .WithSummary("Select the default property workspace")
            .Produces<PropertyResponse>()
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<Ok<IReadOnlyList<PropertyResponse>>> GetAllAsync(
        PropertyService propertyService,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await propertyService.GetAllAsync(cancellationToken));

    private static async Task<Results<Created<PropertyResponse>, ProblemHttpResult>> CreateAsync(
        CreatePropertyRequest request,
        PropertyService propertyService,
        CancellationToken cancellationToken)
    {
        var property = await propertyService.CreateAsync(request.Name, cancellationToken);
        return property is null
            ? TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A property name between 1 and 128 characters is required.")
            : TypedResults.Created($"/api/properties/{property.Id}", property);
    }

    private static async Task<Results<Ok<PropertyResponse>, NotFound>> SetDefaultAsync(
        Guid id,
        PropertyService propertyService,
        CancellationToken cancellationToken)
    {
        var property = await propertyService.SetDefaultAsync(id, cancellationToken);
        return property is null ? TypedResults.NotFound() : TypedResults.Ok(property);
    }
}
