using System.Security.Cryptography;

namespace Despachos.Api.Middleware;

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private const string ApiKeyHeader = "X-API-Key";

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        if (path == "/health" || path == "/health/live")
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var extractedApiKey))
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/xml";
            await context.Response.WriteAsync("<ErrorResponse><Status>ERROR</Status><Messages><Message Field=\"Auth\">X-API-Key header requerido</Message></Messages></ErrorResponse>");
            return;
        }

        var configuredApiKey = context.RequestServices.GetRequiredService<IConfiguration>()["ApiKey"] ?? "";

        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            await _next(context);
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(extractedApiKey!),
                System.Text.Encoding.UTF8.GetBytes(configuredApiKey)))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/xml";
            await context.Response.WriteAsync("<ErrorResponse><Status>ERROR</Status><Messages><Message Field=\"Auth\">X-API-Key invalida</Message></Messages></ErrorResponse>");
            return;
        }

        await _next(context);
    }
}
