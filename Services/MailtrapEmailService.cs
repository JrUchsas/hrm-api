using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HrmApi.Services;

public sealed class MailtrapEmailService : IEmailService
{
    private readonly string _apiToken;
    private readonly string _fromEmail;
    private readonly string _fromName;
    private readonly ILogger<MailtrapEmailService>? _logger;
    private readonly HttpClient _httpClient;

    public MailtrapEmailService(IConfiguration configuration, ILogger<MailtrapEmailService>? logger = null, HttpClient? httpClient = null)
    {
        _apiToken = configuration["Email:Mailtrap:ApiToken"] ?? "";
        _fromEmail = configuration["Email:FromEmail"] ?? "noreply@hrmapi.local";
        _fromName = configuration["Email:FromName"] ?? "HRM API";
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri("https://sandbox.api.mailtrap.io/");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiToken);
    }

    public async Task<EmailResult> SendPasswordResetEmailAsync(string toEmail, string resetLink, string userName, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("SendPasswordResetEmailAsync called for {Email}", toEmail);

        if (string.IsNullOrWhiteSpace(_apiToken))
        {
            _logger?.LogInformation("Development mode: API token empty. Password reset email logged for {Email}", toEmail);
            Console.WriteLine($"[DEV MODE] Password reset email would be sent to: {toEmail}");
            Console.WriteLine($"[DEV MODE] Reset link: {resetLink}");
            return new EmailResult(true, "Development mode: email logged to console");
        }

        try
        {
            var subject = "Reset Your Password - HRM API";
            var body = BuildResetEmailBody(userName, resetLink);

            var request = new
            {
                from = new { email = _fromEmail, name = _fromName },
                to = new[] { new { email = toEmail } },
                subject = subject,
                html = body,
                category = "Password Reset"
            };

            var response = await _httpClient.PostAsJsonAsync("api/send", request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger?.LogInformation("Password reset email sent successfully to {Email} via Mailtrap API", toEmail);
                return new EmailResult(true);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogError("Mailtrap API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new EmailResult(false, $"Mailtrap API error: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send password reset email to {Email}", toEmail);
            Console.Error.WriteLine($"[EMAIL ERROR] Failed to send password reset email: {ex.Message}");
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