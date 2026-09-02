namespace WorkoutTrackerAPI.Services;

public interface IEmailService
{
    Task SendPasswordResetEmailAsync(string toEmail, string token, int ttlMinutes);
    Task SendPasswordChangedNotificationAsync(string toEmail);
}
