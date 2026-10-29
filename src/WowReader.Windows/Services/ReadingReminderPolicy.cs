using System.Globalization;

namespace WowReader.Windows.Services;

public static class ReadingReminderPolicy
{
    public static bool IsDue(bool enabled, DateOnly today, TimeOnly now, int hour, int minute, string? lastDeliveredDate)
    {
        if (!enabled) return false;
        var key = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.Equals(lastDeliveredDate, key, StringComparison.Ordinal)) return false;
        var dueAt = new TimeOnly(Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
        return now >= dueAt;
    }

    public static string BuildMessage(bool onlyIfUnread, int todaySeconds, bool streakNudge, int currentStreak)
    {
        if (onlyIfUnread && todaySeconds > 0) return "";
        if (streakNudge && todaySeconds <= 0 && currentStreak > 0)
            return $"Your {currentStreak}-day reading streak is ready for today. Open a book for a few minutes.";
        return "Take a quiet moment to read today. Your library and reading history stay on this PC.";
    }

    public static string DateKey(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
