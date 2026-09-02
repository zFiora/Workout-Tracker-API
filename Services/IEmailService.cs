namespace WorkoutTrackerAPI.Services;

public interface IEmailService
{
    Task SendPasswordResetEmailAsync(string email, string resetUrl, string token, int expirationMinutes);
    Task SendPasswordChangedNotificationAsync(string email);
}
