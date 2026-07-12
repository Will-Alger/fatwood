using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Email;

namespace ResearchDiscovery.Api.Controllers;

/// <summary>
/// Entra custom-authentication-extension callbacks. The OTP-send hook lets us
/// mail verification codes as Fatwood-branded email instead of Microsoft's
/// stock template. Hard budget from Entra: 2 seconds — we only wait for ACS
/// to ACCEPT the message. Any non-200 (including our own send failure) makes
/// Entra fall back to its built-in email, so sign-ups never brick on us.
/// </summary>
[ApiController]
[Route("api/auth-events")]
public class AuthEventsController(
    OtpEmailSender emailSender,
    ILogger<AuthEventsController> logger) : ControllerBase
{
    public const string Scheme = "AuthEvents";

    private const string ContinueResponseJson = """
        {"data":{"@odata.type":"microsoft.graph.OnOtpSendResponseData","actions":[{"@odata.type":"microsoft.graph.OtpSend.continueWithDefaultBehavior"}]}}
        """;

    [HttpPost("otp-send")]
    [Authorize(AuthenticationSchemes = Scheme)]
    public async Task<IActionResult> OtpSend([FromBody] JsonElement payload, CancellationToken ct)
    {
        // Defense in depth: the token must come from Microsoft's fixed
        // custom-extension caller, not merely be any valid token for us.
        var caller = User.FindFirstValue("azp") ?? User.FindFirstValue("appid");
        if (!string.Equals(caller, AuthEventsOptions.MicrosoftCallerAppId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("OTP event from unexpected caller {Caller}", caller);
            return Forbid();
        }

        if (!TryExtractOtp(payload, out var recipient, out var code, out var requestType))
        {
            logger.LogError("OTP event payload missing identifier/code");
            return Problem(statusCode: StatusCodes.Status400BadRequest);
        }

        var sent = await emailSender.SendAsync(recipient, code, requestType, ct);
        if (!sent)
        {
            // Non-200 → Entra's fallbackToMicrosoftProviderOnError sends its
            // default email instead. The user still gets a code.
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Content(ContinueResponseJson, "application/json");
    }

    /// <summary>
    /// data.otpContext.{identifier,oneTimeCode} — case-insensitively, because
    /// Microsoft's docs and samples disagree on the casing of oneTimeCode.
    /// </summary>
    public static bool TryExtractOtp(
        JsonElement payload, out string recipient, out string code, out string requestType)
    {
        recipient = string.Empty;
        code = string.Empty;
        requestType = string.Empty;

        if (!TryGetPropertyIgnoreCase(payload, "data", out var data) ||
            !TryGetPropertyIgnoreCase(data, "otpContext", out var otp))
        {
            return false;
        }

        if (TryGetPropertyIgnoreCase(otp, "identifier", out var id))
        {
            recipient = id.GetString() ?? string.Empty;
        }

        if (TryGetPropertyIgnoreCase(otp, "oneTimeCode", out var otpCode))
        {
            code = otpCode.GetString() ?? string.Empty;
        }

        if (TryGetPropertyIgnoreCase(data, "authenticationContext", out var authCtx) &&
            TryGetPropertyIgnoreCase(authCtx, "requestType", out var type))
        {
            requestType = type.GetString() ?? string.Empty;
        }

        return recipient.Length > 0 && code.Length > 0;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
