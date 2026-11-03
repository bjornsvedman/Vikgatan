using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceManager.Api.Middleware;

internal sealed class BadHttpRequestExceptionHandler(ILogger<BadHttpRequestExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        logger.LogInformation(
            "Rejected malformed HTTP request with status code {StatusCode}.",
            badRequest.StatusCode);

        var statusCode = badRequest.StatusCode is >= 400 and < 500
            ? badRequest.StatusCode
            : StatusCodes.Status400BadRequest;

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = "The request is malformed or incomplete.",
            Detail = "Check the request content type and form fields.",
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier }
        }, cancellationToken);

        return true;
    }
}