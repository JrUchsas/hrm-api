using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HrmApi.Services;

namespace HrmApi.Services;

public sealed class MailgunEmailService(HttpClient httpClient, IConfiguration configuration) : IEmailService
{
    private readonly string _domain = configuration["Email:Mailgun:Domain"] ?? "";
    private readonly string _apiKey = configuration["Email:Mailgun:ApiKey"] ?? "";
    private readonly string _fromEmail = configuration["Email:FromEmail"] ?? "noreply@hrmapi.local";
    private readonly string _fromName = configuration["Email:FromName"] ?? "HRM API";

    public async Task<EmailResult> SendPasswordResetEmailAsync(string toEmail, string resetLink, string userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_domain) || string.IsNullOrWhiteSpace(_apiKey))
        {
            return new EmailResult(false, "Mailgun not configured");
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.mailgun.net/v3/{_domain}/messages")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["from"] = $"{_fromName} <{_fromEmail}>",
                    ["to"] = toEmail,
                    ["subject"] = "Reset Your Password - HRM API",
                    ["html"] = BuildResetEmailBody(userName, resetLink)
                })
            };

            var authBytes = System.Text.Encoding.ASCII.GetBytes($"api:{_apiKey}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

            var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new EmailResult(true);
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"[MAILGUN ERROR] {response.StatusCode}: {errorContent}");
            return new EmailResult(false, "Failed to send email");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MAILGUN ERROR] Failed to send password reset email: {ex.Message}");
            return new EmailResult(false, "Failed to send email");
        }
    }

    private static string BuildResetEmailBody(string userName, string resetLink)
    {
        var safeUserName = System.Net.WebUtility.HtmlEncode(userName);
        var safeResetLink = System.Net.WebUtility.HtmlEncode(resetLink);

        return $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
            </head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px;">
                <div style="background: #f8f9fa; border-radius: 8px; padding: 32px;">
                    <h1 style="color: #1a1a2e; margin-top: 0; font-size: 24px;">Reset Your Password</h1>

                    <p>Hi {safeUserName},</p>

                    <p>You requested to reset your password for your HRM API account. Click the button below to create a new password:</p>

                    <div style="text-align: center; margin: 32px 0;">
                        <a href="{safeResetLink}" style="background: #2563eb; color: white; padding: 14px 28px; border-radius: 6px; text-decoration: none; font-weight: 600; display: inline-block;">
                            Reset Password
                        </a>
                    </div>

                    <p style="font-size: 14px; color: #666;">Or copy this link into your browser:</p>
                    <p style="font-size: 12px; color: #999; word-break: break-all;">{safeResetLink}</p>

                    <hr style="border: none; border-top: 1px solid #e0e0e0; margin: 24px 0;">

                    <p style="font-size: 14px; color: #666; margin: 0;">
                        This link will expire in 1 hour. If you didn't request this, please ignore this email or contact support.
                    </p>

                    <p style="font-size: 14px; color: #666; margin-top: 24px;">
                        — The HRM API Team
                    </p>
                </div>
            </body>
            </html>
            """;
    }
}