using Resend;

namespace WorkoutTrackerAPI.Services;

public class ResendEmailService(IResend resend, IConfiguration config, ILogger<ResendEmailService> logger) : IEmailService
{
    public Task SendPasswordResetEmailAsync(string email, string resetUrl, string token, int expirationMinutes)
    {
        var subject = "Reset your Workout Tracker password";
        var html =
            $"""
             <div style="font-family: -apple-system, Segoe UI, Roboto, sans-serif; max-width: 480px; margin: 0 auto; color: #1a1a1a;">
               <h2 style="margin-bottom: 4px;">Workout Tracker</h2>
               <p>We received a request to reset your password. This link and code expire in {expirationMinutes} minutes.</p>
               <p style="text-align: center; margin: 32px 0;">
                 <a href="{resetUrl}" style="background: #2563eb; color: #ffffff; padding: 12px 24px; border-radius: 8px; text-decoration: none; font-weight: 600; display: inline-block;">Reset Password</a>
               </p>
               <p>If the button doesn't open the app, use this code instead:</p>
               <p style="font-size: 20px; font-weight: 700; letter-spacing: 1px; background: #f3f4f6; padding: 12px 16px; border-radius: 8px; text-align: center;">{token}</p>
               <p style="color: #6b7280; font-size: 13px; margin-top: 32px;">If you didn't request this, you can safely ignore this email — your password won't be changed.</p>
             </div>
             """;
        var text =
            $"""
             We received a request to reset your Workout Tracker password.

             Reset link: {resetUrl}

             Or paste this code into the app: {token}

             This expires in {expirationMinutes} minutes. If you didn't request this, you can safely ignore this email.
             """;

        return SendAsync(email, subject, html, text);
    }

    public Task SendPasswordChangedNotificationAsync(string email) =>
        SendAsync(
            email,
            "Your Workout Tracker password was changed",
            "<p>Your password was just changed. If this wasn't you, please contact support immediately.</p>",
            "Your password was just changed. If this wasn't you, please contact support immediately.");

    private async Task SendAsync(string toEmail, string subject, string htmlBody, string textBody)
    {
        var fromEmail = config["PasswordReset:FromEmail"];
        if (string.IsNullOrWhiteSpace(fromEmail)) fromEmail = "onboarding@resend.dev";
        var fromName = config["PasswordReset:FromName"];
        if (string.IsNullOrWhiteSpace(fromName)) fromName = "Workout Tracker";

        var message = new EmailMessage
        {
            From = $"{fromName} <{fromEmail}>",
            Subject = subject,
            HtmlBody = htmlBody,
            TextBody = textBody,
        };
        message.To.Add(toEmail);

        try
        {
            var response = await resend.EmailSendAsync(message);
            if (!response.Success)
                logger.LogError("Resend failed to send email to {Email}: {Error}", toEmail, response.Exception?.Message);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed send must never surface as a 500, especially on
            // forgot-password where the response can't reveal anything went wrong.
            logger.LogError(ex, "Unexpected error sending email to {Email}", toEmail);
        }
    }
}
