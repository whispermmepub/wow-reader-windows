using System.Globalization;

namespace WowReader.Windows.Models;

public sealed class ReaderSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string ReadingMode { get; set; } = "page";
    public string PageAnimation { get; set; } = "none";
    public int FontSizePercent { get; set; } = 100;
    public int FontWeight { get; set; } = 400;
    public int LineHeightPercent { get; set; } = 160;
    public int MarginPercent { get; set; } = 5;
    public string FormattingMode { get; set; } = "original";
    public string TextAlignment { get; set; } = "original";
    public bool AutoSpacing { get; set; } = true;
    public string Theme { get; set; } = "light";
    public string CustomTextColor { get; set; } = "";
    public string CustomBackgroundColor { get; set; } = "";
    public string NightLight { get; set; } = "off";
    public int BrightnessPercent { get; set; } = -1;
    public bool KeepScreenOn { get; set; }
    public bool AutoScrollEnabled { get; set; }
    public int AutoScrollSpeed { get; set; } = 4;
    public bool AutoScrollWholeBook { get; set; } = true;
    public bool ReadingRulerEnabled { get; set; }
    public int ReadingRulerLines { get; set; } = 1;
    public bool EyeBreakReminder { get; set; } = true;
    public int PositionDisplayMode { get; set; }
    public bool DailyReadingReminderEnabled { get; set; }
    public int DailyReadingReminderHour { get; set; } = 20;
    public int DailyReadingReminderMinute { get; set; }
    public bool DailyReminderOnlyIfUnread { get; set; } = true;
    public bool DailyReminderStreakNudge { get; set; } = true;
    public string DailyReminderLastDeliveredDate { get; set; } = "";

    public ReaderSettings Clone() => (ReaderSettings)MemberwiseClone();

    public void Normalize()
    {
        SchemaVersion = 1;
        ReadingMode = OneOf(ReadingMode, "page", "scroll");
        PageAnimation = OneOf(PageAnimation, "none", "slide", "curl");
        FontSizePercent = Math.Clamp(FontSizePercent, 80, 300);
        FontWeight = Math.Clamp((int)Math.Round(FontWeight / 100d) * 100, 200, 800);
        LineHeightPercent = Math.Clamp((int)Math.Round(LineHeightPercent / 10d) * 10, 120, 220);
        MarginPercent = Math.Clamp(MarginPercent, 1, 14);
        FormattingMode = OneOf(FormattingMode, "original", "reader");
        TextAlignment = OneOf(TextAlignment, "original", "justify", "left", "right");
        Theme = OneOf(Theme, "light", "sepia", "dark", "eink", "premium", "custom");
        CustomTextColor = NormalizeHexColor(CustomTextColor);
        CustomBackgroundColor = NormalizeHexColor(CustomBackgroundColor);
        NightLight = OneOf(NightLight, "off", "auto", "on");
        BrightnessPercent = BrightnessPercent < 0 ? -1 : Math.Clamp(BrightnessPercent, 5, 100);
        AutoScrollSpeed = Math.Clamp(AutoScrollSpeed, 1, 10);
        ReadingRulerLines = Math.Clamp(ReadingRulerLines, 1, 20);
        PositionDisplayMode = Math.Clamp(PositionDisplayMode, 0, 2);
        DailyReadingReminderHour = Math.Clamp(DailyReadingReminderHour, 0, 23);
        DailyReadingReminderMinute = Math.Clamp(DailyReadingReminderMinute, 0, 59);
        DailyReminderLastDeliveredDate ??= "";
        DailyReminderLastDeliveredDate = DateOnly.TryParseExact(DailyReminderLastDeliveredDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? DailyReminderLastDeliveredDate
            : "";
    }

    private static string OneOf(string? value, params string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : allowed[0];

    private static string NormalizeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        var trimmed = value.Trim();
        return trimmed.Length == 7 && trimmed[0] == '#' && trimmed.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? trimmed.ToUpperInvariant()
            : "";
    }
}
