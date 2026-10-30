using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WowReader.Windows.Models;

namespace WowReader.Windows;

public partial class ReaderSettingsWindow : Window
{
    private bool _isInitializing = true;
    private readonly bool _hasCurrentBook;

    public ReaderSettings Settings { get; private set; }
    public bool ResetRequested { get; private set; }
    public bool UseBookTypography => _hasCurrentBook && BookTypographyOverrideCheck.IsChecked == true;

    public ReaderSettingsWindow(ReaderSettings current, bool hasCurrentBook = false, bool hasTypographyOverride = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        InitializeComponent();
        _hasCurrentBook = hasCurrentBook;
        BookTypographyOverrideCheck.IsEnabled = hasCurrentBook;
        BookTypographyOverrideCheck.IsChecked = hasCurrentBook && hasTypographyOverride;
        Settings = current.Clone();
        InitializeValues(Settings);
    }

    private void InitializeValues(ReaderSettings settings)
    {
        _isInitializing = true;
        SetSelection(ReadingModePicker, settings.ReadingMode);
        SetSelection(PageAnimationPicker, settings.PageAnimation == "curl" ? "none" : settings.PageAnimation);
        SetSelection(FormattingPicker, settings.FormattingMode);
        SetSelection(AutoScrollScopePicker, settings.AutoScrollWholeBook ? "whole" : "chapter");
        FontSizeSlider.Value = settings.FontSizePercent;
        FontWeightSlider.Value = settings.FontWeight;
        LineHeightSlider.Value = settings.LineHeightPercent;
        MarginSlider.Value = settings.MarginPercent;
        SetSelection(AlignmentPicker, settings.TextAlignment);
        AutoSpacingCheck.IsChecked = settings.AutoSpacing;
        AutoScrollCheck.IsChecked = settings.AutoScrollEnabled;
        AutoScrollSpeedSlider.Value = settings.AutoScrollSpeed;
        SetSelection(ThemePicker, settings.Theme);
        CustomTextColorBox.Text = settings.CustomTextColor;
        CustomBackgroundColorBox.Text = settings.CustomBackgroundColor;
        SetSelection(NightLightPicker, settings.NightLight);
        SetSelection(BrightnessPicker, settings.BrightnessPercent.ToString());
        KeepScreenOnCheck.IsChecked = settings.KeepScreenOn;
        ReadingRulerCheck.IsChecked = settings.ReadingRulerEnabled;
        RulerLinesSlider.Value = settings.ReadingRulerLines;
        EyeBreakReminderCheck.IsChecked = settings.EyeBreakReminder;
        SetSelection(PositionDisplayPicker, settings.PositionDisplayMode.ToString());
        DailyReminderEnabledCheck.IsChecked = settings.DailyReadingReminderEnabled;
        DailyReminderTimeBox.Text = $"{settings.DailyReadingReminderHour:00}:{settings.DailyReadingReminderMinute:00}";
        DailyReminderOnlyIfUnreadCheck.IsChecked = settings.DailyReminderOnlyIfUnread;
        DailyReminderStreakNudgeCheck.IsChecked = settings.DailyReminderStreakNudge;
        _isInitializing = false;
        UpdateValueLabels();
        UpdateDependentEnabledState();
        UpdateAppearancePreview();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var oldReminderDate = Settings.DailyReminderLastDeliveredDate;
        var reminderEnabled = DailyReminderEnabledCheck.IsChecked == true;
        var validReminderTime = TimeOnly.TryParseExact(DailyReminderTimeBox.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var reminderTime);
        if (!validReminderTime && reminderEnabled)
        {
            MessageBox.Show(this, "Enter the daily reminder time as HH:mm using a 24-hour clock.",
                "Check the reminder time", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!validReminderTime) reminderTime = new TimeOnly(Settings.DailyReadingReminderHour, Settings.DailyReadingReminderMinute);
        if (!TryReadColor(CustomTextColorBox.Text, out var textColor)
            || !TryReadColor(CustomBackgroundColorBox.Text, out var backgroundColor))
        {
            MessageBox.Show(this, "Enter a color as #RRGGBB, or leave it blank to use the theme color.",
                "Check the color value", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Settings = new ReaderSettings
        {
            ReadingMode = Selected(ReadingModePicker, "page"),
            PageAnimation = Selected(PageAnimationPicker, "none"),
            FontSizePercent = Whole(FontSizeSlider.Value),
            FontWeight = Whole(FontWeightSlider.Value),
            LineHeightPercent = Whole(LineHeightSlider.Value),
            MarginPercent = Whole(MarginSlider.Value),
            FormattingMode = Selected(FormattingPicker, "original"),
            TextAlignment = Selected(AlignmentPicker, "original"),
            AutoSpacing = AutoSpacingCheck.IsChecked == true,
            Theme = Selected(ThemePicker, "light"),
            CustomTextColor = textColor,
            CustomBackgroundColor = backgroundColor,
            NightLight = Selected(NightLightPicker, "off"),
            BrightnessPercent = int.TryParse(Selected(BrightnessPicker, "-1"), out var brightness) ? brightness : -1,
            KeepScreenOn = KeepScreenOnCheck.IsChecked == true,
            AutoScrollEnabled = AutoScrollCheck.IsChecked == true,
            AutoScrollSpeed = Whole(AutoScrollSpeedSlider.Value),
            AutoScrollWholeBook = Selected(AutoScrollScopePicker, "whole") == "whole",
            ReadingRulerEnabled = ReadingRulerCheck.IsChecked == true,
            ReadingRulerLines = Whole(RulerLinesSlider.Value),
            EyeBreakReminder = EyeBreakReminderCheck.IsChecked == true,
            PositionDisplayMode = int.TryParse(Selected(PositionDisplayPicker, "0"), out var positionMode) ? positionMode : 0,
            DailyReadingReminderEnabled = DailyReminderEnabledCheck.IsChecked == true,
            DailyReadingReminderHour = reminderTime.Hour,
            DailyReadingReminderMinute = reminderTime.Minute,
            DailyReminderOnlyIfUnread = DailyReminderOnlyIfUnreadCheck.IsChecked == true,
            DailyReminderStreakNudge = DailyReminderStreakNudgeCheck.IsChecked == true,
            DailyReminderLastDeliveredDate = oldReminderDate
        };
        Settings.Normalize();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Restore the default Windows reader settings? Your books and saved reading positions will not be changed.",
            "Reset reader settings", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        ResetRequested = true;
        InitializeValues(new ReaderSettings());
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            UpdateDependentEnabledState();
        }
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing && ThemePicker is not null && CustomTextColorBox is not null
            && CustomBackgroundColorBox is not null && Selected(ThemePicker, "light") != "custom")
        {
            CustomTextColorBox.Clear();
            CustomBackgroundColorBox.Clear();
        }
        UpdateAppearancePreview();
        if (!_isInitializing) UpdateDependentEnabledState();
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAppearancePreview();

    private void UpdateAppearancePreview()
    {
        if (ThemePicker is null || CustomTextColorBox is null || CustomBackgroundColorBox is null
            || AppearancePreviewBorder is null || AppearancePreviewText is null) return;
        var (background, foreground) = Selected(ThemePicker, "light") switch
        {
            "sepia" => ("#F4ECD8", "#4A4033"),
            "dark" => ("#121212", "#E8EAED"),
            "eink" => ("#E6E5DE", "#2C3130"),
            "premium" => ("#EDF3EC", "#20382B"),
            "custom" => ("#FAFAFA", "#202124"),
            _ => ("#FFFFFF", "#202124")
        };
        AppearancePreviewBorder.Background = new SolidColorBrush(ParsePreviewColor(CustomBackgroundColorBox.Text, background));
        AppearancePreviewText.Foreground = new SolidColorBrush(ParsePreviewColor(CustomTextColorBox.Text, foreground));
    }

    private static Color ParsePreviewColor(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!TryReadColor(candidate, out var normalized)) normalized = fallback;
        return Color.FromRgb(
            byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private void ControlChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            UpdateDependentEnabledState();
        }
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isInitializing) UpdateValueLabels(); }
    private void FontWeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isInitializing) UpdateValueLabels(); }
    private void LineHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isInitializing) UpdateValueLabels(); }
    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isInitializing) UpdateValueLabels(); }
    private void AutoScrollSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isInitializing) UpdateValueLabels(); }
    private void RulerLinesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isInitializing) UpdateValueLabels(); }

    private void UpdateValueLabels()
    {
        if (_isInitializing || FontSizeSlider is null || FontWeightSlider is null || LineHeightSlider is null
            || MarginSlider is null || AutoScrollSpeedSlider is null || RulerLinesSlider is null
            || FontSizeValue is null || FontWeightValue is null || LineHeightValue is null
            || MarginValue is null || AutoScrollSpeedValue is null || RulerLinesValue is null)
        {
            return;
        }
        FontSizeValue.Text = $"{Whole(FontSizeSlider.Value)}%";
        FontWeightValue.Text = Whole(FontWeightSlider.Value).ToString();
        LineHeightValue.Text = $"{Whole(LineHeightSlider.Value) / 100d:0.00}×";
        MarginValue.Text = $"{Whole(MarginSlider.Value)}%";
        AutoScrollSpeedValue.Text = $"{Whole(AutoScrollSpeedSlider.Value)} / 10";
        RulerLinesValue.Text = $"{Whole(RulerLinesSlider.Value)} lines";
    }

    private void UpdateDependentEnabledState()
    {
        if (ReadingModePicker is null || AutoScrollCheck is null || AutoScrollSpeedSlider is null
            || AutoScrollScopePicker is null || FormattingPicker is null || FontWeightSlider is null
            || LineHeightSlider is null || MarginSlider is null || AlignmentPicker is null
            || AutoSpacingCheck is null || DailyReminderEnabledCheck is null || DailyReminderTimeBox is null
            || DailyReminderOnlyIfUnreadCheck is null || DailyReminderStreakNudgeCheck is null)
        {
            return;
        }
        var scrollMode = Selected(ReadingModePicker, "page") == "scroll";
        AutoScrollCheck.IsEnabled = scrollMode;
        AutoScrollSpeedSlider.IsEnabled = scrollMode && AutoScrollCheck.IsChecked == true;
        AutoScrollScopePicker.IsEnabled = scrollMode && AutoScrollCheck.IsChecked == true;
        var readerFormatting = Selected(FormattingPicker, "original") == "reader";
        FontWeightSlider.IsEnabled = readerFormatting;
        LineHeightSlider.IsEnabled = readerFormatting;
        MarginSlider.IsEnabled = readerFormatting;
        AlignmentPicker.IsEnabled = readerFormatting;
        AutoSpacingCheck.IsEnabled = readerFormatting && Selected(AlignmentPicker, "original") == "justify";
        var dailyReminderEnabled = DailyReminderEnabledCheck.IsChecked == true;
        DailyReminderTimeBox.IsEnabled = dailyReminderEnabled;
        DailyReminderOnlyIfUnreadCheck.IsEnabled = dailyReminderEnabled;
        DailyReminderStreakNudgeCheck.IsEnabled = dailyReminderEnabled;
    }

    private static int Whole(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static string Selected(ComboBox box, string fallback) => box.SelectedValue as string ?? fallback;

    private static void SetSelection(ComboBox box, string value)
    {
        box.SelectedValue = value;
        if (box.SelectedIndex < 0 && box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static bool TryReadColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        var text = value.Trim();
        if (!Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant))
        {
            return false;
        }
        normalized = text.ToUpperInvariant();
        return true;
    }
}
