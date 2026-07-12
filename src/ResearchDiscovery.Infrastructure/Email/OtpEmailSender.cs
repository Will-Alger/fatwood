using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Email;

/// <summary>
/// Sends the Fatwood-branded verification-code email. Called from the Entra
/// OTP event hook, which has a 2-second budget: we wait only until ACS
/// ACCEPTS the message (WaitUntil.Started), never for delivery.
/// </summary>
public class OtpEmailSender(IOptions<EmailOptions> options, ILogger<OtpEmailSender> logger)
{
    private readonly Lazy<EmailClient?> _client = new(() =>
        options.Value.Enabled ? new EmailClient(options.Value.AcsConnectionString) : null);

    public bool Enabled => options.Value.Enabled;

    /// <returns>False when sending is disabled or ACS rejected the message —
    /// the caller then signals Entra to fall back to its own email.</returns>
    public async Task<bool> SendAsync(
        string recipient, string code, string requestType, CancellationToken ct)
    {
        var client = _client.Value;
        if (client is null)
        {
            return false;
        }

        var intro = requestType switch
        {
            "passwordReset" => "Use this code to reset your Fatwood password.",
            "signIn" => "Use this code to sign in to Fatwood.",
            _ => "Welcome! Use this code to verify your email and finish creating your account.",
        };

        try
        {
            await client.SendAsync(
                WaitUntil.Started,
                options.Value.From,
                recipient,
                subject: $"{code} is your Fatwood verification code",
                htmlContent: BuildHtml(code, intro),
                plainTextContent:
                    $"{intro}\n\nYour verification code: {code}\n\n" +
                    "If you didn't request this, you can ignore this email.\n— Fatwood · fatwood.io",
                cancellationToken: ct);
            logger.LogInformation("OTP email accepted by ACS for {Recipient} ({Type})",
                recipient, requestType);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ACS rejected the OTP email for {Recipient}", recipient);
            return false;
        }
    }

    /// <summary>
    /// Email-client-safe HTML: table layout, inline styles, no external
    /// images (the wordmark is styled text, so nothing gets blocked).
    /// </summary>
    internal static string BuildHtml(string code, string intro) => $$"""
        <!doctype html>
        <html>
        <body style="margin:0;padding:0;background-color:#f4f4f5;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#f4f4f5;padding:32px 16px;">
            <tr><td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:460px;background-color:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #e4e4e7;">
                <tr>
                  <td style="background-color:#141416;padding:20px 32px;">
                    <img src="https://www.fatwood.io/email-banner.png" width="245" height="36" alt="Fatwood"
                         style="display:block;border:0;outline:none;max-width:245px;height:auto;" />
                  </td>
                </tr>
                <tr>
                  <td style="padding:32px;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#3a3a3e;font-size:15px;line-height:1.6;">
                    <p style="margin:0 0 20px;">{{intro}}</p>
                    <div style="background-color:#141416;border-radius:10px;padding:18px 24px;text-align:center;margin:0 0 20px;">
                      <span style="font-family:'Courier New',Consolas,monospace;font-size:28px;letter-spacing:6px;color:#d97a45;font-weight:bold;">{{code}}</span>
                    </div>
                    <p style="margin:0;color:#8a8a90;font-size:13px;">
                      The code expires shortly. If you didn't request it, you can safely ignore this email.
                    </p>
                  </td>
                </tr>
                <tr>
                  <td style="padding:16px 32px;border-top:1px solid #e4e4e7;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#8a8a90;font-size:12px;">
                    Fatwood — kindling for your next build · <a href="https://www.fatwood.io" style="color:#d97a45;text-decoration:none;">fatwood.io</a>
                  </td>
                </tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;
}
