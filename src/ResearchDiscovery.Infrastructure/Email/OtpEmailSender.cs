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
    /// Email-client-safe HTML in the Char/Flashpoint theme: a char header band
    /// over a limestone page, square corners, hairline rules, and the one
    /// ember reserved for the code itself — the only thing here that catches.
    /// <para>
    /// Table layout and inline styles only. The wordmark is styled text rather
    /// than the old hosted banner PNG, so it survives the image blocking most
    /// clients apply by default and can never drift from the app's brand.
    /// Webfonts do not load in email, so the stacks fall back the same way the
    /// app's own do: Georgia for prose, Courier for the code.
    /// </para>
    /// </summary>
    internal static string BuildHtml(string code, string intro) => $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="color-scheme" content="light">
          <meta name="supported-color-schemes" content="light">
        </head>
        <body style="margin:0;padding:0;background-color:#e7e4dc;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#e7e4dc;padding:32px 16px;">
            <tr><td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:460px;background-color:#f2f0ea;border:1px solid #cfcabf;">
                <tr>
                  <td style="background-color:#0d0d0c;padding:22px 32px;font-family:'Helvetica Neue',Helvetica,Arial,sans-serif;">
                    <div style="font-size:19px;font-weight:bold;letter-spacing:3px;text-transform:uppercase;color:#e9e5dc;line-height:1.15;">Fatwood</div>
                    <div style="font-size:12px;color:#999389;letter-spacing:0.5px;padding-top:4px;">Kindling for your next build.</div>
                  </td>
                </tr>
                <tr>
                  <td style="padding:32px;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#191814;font-size:15px;line-height:1.6;">
                    <p style="margin:0 0 22px;font-family:Georgia,'Times New Roman',serif;font-size:16px;line-height:1.55;">{{intro}}</p>
                    <div style="background-color:#0d0d0c;padding:20px 24px;text-align:center;margin:0 0 22px;">
                      <div style="font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:10px;letter-spacing:2px;text-transform:uppercase;color:#837e75;padding-bottom:10px;">Verification code</div>
                      <span style="font-family:'Courier New',Consolas,monospace;font-size:28px;letter-spacing:6px;color:#de4e33;font-weight:bold;">{{code}}</span>
                    </div>
                    <p style="margin:0;color:#565148;font-size:13px;">
                      The code expires shortly. If you didn't request it, you can safely ignore this email.
                    </p>
                  </td>
                </tr>
                <tr>
                  <td style="padding:16px 32px;border-top:1px solid #cfcabf;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#565148;font-size:12px;">
                    Fatwood — kindling for your next build · <a href="https://www.fatwood.io" style="color:#a82a17;text-decoration:none;">fatwood.io</a>
                  </td>
                </tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;
}
