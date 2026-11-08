using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Api.Exceptions;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> logger;
    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        this.logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        int statusCode = exception switch
        {
            DbUpdateConcurrencyException =>
                StatusCodes.Status409Conflict,

            KeyNotFoundException =>
                StatusCodes.Status404NotFound,

            ArgumentException =>
                StatusCodes.Status400BadRequest,

            _ => StatusCodes.Status500InternalServerError
        };

        logger.LogError(
            exception,
            "Request failed. StatusCode: {StatusCode}",
            statusCode);

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode switch
            {
                400 => "Bad Request",
                404 => "Not Found",
                409 => "Conflict",
                _ => "Internal Server Error"
            },
            Detail = statusCode == 500
                ? "An unexpected error occurred."
                : exception.Message,
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(
            problem,
            cancellationToken);

        return true;
    }
}