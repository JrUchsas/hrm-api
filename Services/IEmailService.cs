namespace HrmApi.Services;

public interface IEmailService
{
    Task<EmailResult> SendPasswordResetEmailAsync(string toEmail, string resetLink, string userName, CancellationToken cancellationToken = default);
}

public sealed record EmailResult(bool Success, string? ErrorMessage = null);