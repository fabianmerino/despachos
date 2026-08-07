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

        // Cada consumidor externo (SAP PI para el SOAP inbound, el servicio de captura
        // para el webhook) tiene sus propias credenciales dedicadas: no comparten secreto.
        var (userKey, passKey) = path.StartsWith("/webhooks/despacho-completado", StringComparison.Ordinal)
            ? ("WebhookCompletado:Username", "WebhookCompletado:Password")
            : ("SapInbound:Username", "SapInbound:Password");

        var expectedUser = config[userKey] ?? "";
        var expectedPass = config[passKey] ?? "";

        // Sin credenciales configuradas se deniega (fail-closed): el arranque del servicio ya
        // deberia haber fallado en este caso (ver Program.cs), pero este chequeo evita que el
        // endpoint quede abierto sin autenticacion si esa validacion se elimina o se evita.
        if (string.IsNullOrWhiteSpace(expectedUser))
        {
            await Deny(context);
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
