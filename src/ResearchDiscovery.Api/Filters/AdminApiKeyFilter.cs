using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Api.Filters;

/// <summary>
/// Guards the admin ingestion endpoints. When no admin API key is configured
/// the endpoints return 404 — a deployment without the secret exposes no admin
/// surface at all, which keeps raw ingestion unreachable for regular users.
/// </summary>
public class AdminApiKeyFilter(IOptions<AdminOptions> options) : IAsyncActionFilter
{
    public const string HeaderName = "X-Admin-Api-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var admin = options.Value;
        if (!admin.Enabled)
        {
            context.Result = new NotFoundResult();
            return;
        }

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!FixedTimeEquals(provided, admin.ApiKey))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        await next();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
