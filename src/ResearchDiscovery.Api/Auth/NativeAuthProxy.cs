using System.Net.Http.Headers;

namespace ResearchDiscovery.Api.Auth;

/// <summary>
/// Same-origin reverse proxy for Entra External ID's native authentication
/// API. Those endpoints don't support CORS by design, so the browser SDK
/// posts to us and we forward to the tenant — which also means sign-in
/// traffic never leaves fatwood.io from the user's point of view.
/// Only the documented native-auth paths are forwardable, and the route is
/// rate-limited per IP: Entra sees our egress IP, so brute-force braking is
/// our job here.
/// </summary>
public static class NativeAuthProxy
{
    public const string HttpClientName = "NativeAuthProxy";

    private static readonly HashSet<string> AllowedPaths = new(StringComparer.Ordinal)
    {
        "signup/v1.0/start",
        "signup/v1.0/challenge",
        "signup/v1.0/continue",
        "oauth2/v2.0/initiate",
        "oauth2/v2.0/challenge",
        "oauth2/v2.0/token",
        "oauth2/v2.0/introspect",
        "password_reset/v1.0/start",
        "password_reset/v1.0/challenge",
        "password_reset/v1.0/continue",
    };

    // Telemetry headers the MSAL custom-auth SDK sends; everything else
    // (cookies, origin, auth headers) is deliberately NOT forwarded.
    private static readonly string[] ForwardedHeaders =
    [
        "x-client-SKU", "x-client-VER", "x-client-OS", "x-client-CPU",
        "x-client-current-telemetry", "x-client-last-telemetry", "client-request-id",
    ];

    /// <param name="authority">Auth:Authority (…/tenantId/v2.0); the proxy
    /// targets the tenant base one level up. Null/empty = proxy disabled.</param>
    public static void MapNativeAuthProxy(this WebApplication app, string? authority)
    {
        string? baseUrl = null;
        if (!string.IsNullOrWhiteSpace(authority))
        {
            var trimmed = authority.TrimEnd('/');
            baseUrl = trimmed.EndsWith("/v2.0", StringComparison.Ordinal)
                ? trimmed[..^"/v2.0".Length]
                : trimmed;
        }

        app.MapPost("/auth-proxy/{**path}", async (
            string path,
            HttpContext context,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            if (!AllowedPaths.Contains(path))
            {
                return Results.NotFound();
            }

            if (baseUrl is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    detail: "Sign-in is not configured on this deployment.");
            }

            var client = httpFactory.CreateClient(HttpClientName);
            using var upstream = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{path}");
            upstream.Content = new StreamContent(context.Request.Body);
            upstream.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            foreach (var name in ForwardedHeaders)
            {
                if (context.Request.Headers.TryGetValue(name, out var value))
                {
                    upstream.Headers.TryAddWithoutValidation(name, (string?)value);
                }
            }

            using var response = await client.SendAsync(
                upstream, HttpCompletionOption.ResponseHeadersRead, ct);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType =
                response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(context.Response.Body, ct);
            return Results.Empty;
        }).RequireRateLimiting("auth");
    }
}
