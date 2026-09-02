using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace WorkoutTrackerAPI.Services;

// Reads SMTP_* config keys directly (matches the Railway env var names verbatim —
// no nested "Smtp:" section) so ops can set them straight in the Railway dashboard.
public class SmtpEmailService(IConfiguration config, ILogger<SmtpEmailService> logger) : IEmailService
{
    public Task SendPasswordResetEmailAsync(string toEmail, string token, int ttlMinutes) =>
        SendAsync(
            toEmail,
            "Reset your WorkoutTracker password",
            $"""
             We received a request to reset your WorkoutTracker password.

             Your reset code: {token}

             Paste this code into the app to choose a new password. It expires in {ttlMinutes} minutes.

             If you didn't request this, you can safely ignore this email.
             """);

    public Task SendPasswordChangedNotificationAsync(string toEmail) =>
        SendAsync(
            toEmail,
            "Your WorkoutTracker password was changed",
            "Your password was just changed. If this wasn't you, contact support immediately.");

    private async Task SendAsync(string toEmail, string subject, string body)
    {
        var host = config["SMTP_HOST"];
        if (string.IsNullOrWhiteSpace(host))
        {
            logger.LogWarning("SMTP_HOST not configured; skipping email to {Email}", toEmail);
            return;
        }

        var port = int.TryParse(config["SMTP_PORT"], out var p) ? p : 587;
        var username = config["SMTP_USERNAME"];
        var password = config["SMTP_PASSWORD"];
        var from = config["SMTP_FROM_ADDRESS"];
        if (string.IsNullOrWhiteSpace(from)) from = username;
        if (string.IsNullOrWhiteSpace(from))
        {
            logger.LogWarning("No SMTP_FROM_ADDRESS/SMTP_USERNAME configured; skipping email to {Email}", toEmail);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTlsWhenAvailable);
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                await client.AuthenticateAsync(username, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed send must never surface as a 500 to the caller,
            // especially on forgot-password where the response can't reveal anything went wrong.
            logger.LogError(ex, "Failed to send email to {Email}", toEmail);
        }
    }
}
