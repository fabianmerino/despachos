using System.Security.Cryptography;
using System.Text;

namespace Despachos.Api.Middleware;

public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private const string Prefix = "Basic ";
    private const string Realm = "despachos";

    public BasicAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        if (path == "/health" || path == "/health/live" || path == "/health/ready")
        {
            await _next(context);
            return;
        }

        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        var expectedUser = config["SapInbound:Username"] ?? "";
        var expectedPass = config["SapInbound:Password"] ?? "";

        if (string.IsNullOrWhiteSpace(expectedUser))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            await Deny(context);
            return;
        }

        string user;
        string pass;
        try
        {
            var encoded = authHeader[Prefix.Length..].Trim();
            var bytes = Convert.FromBase64String(encoded);
            var decoded = Encoding.UTF8.GetString(bytes);
            var idx = decoded.IndexOf(':');
            if (idx < 0)
            {
                await Deny(context);
                return;
            }
            user = decoded[..idx];
            pass = decoded[(idx + 1)..];
        }
        catch
        {
            await Deny(context);
            return;
        }

        if (!FixedTimeEquals(user, expectedUser) || !FixedTimeEquals(pass, expectedPass))
        {
            await Deny(context);
            return;
        }

        await _next(context);
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task Deny(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\"";
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("Unauthorized");
    }
}
