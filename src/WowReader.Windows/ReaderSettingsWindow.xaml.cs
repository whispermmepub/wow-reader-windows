using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WowReader.Windows.Models;

namespace WowReader.Windows;

public partial class ReaderSettingsWindow : Window
{
    private bool _isInitializing;

    public ReaderSettings Settings { get; private set; }
    public bool ResetRequested { get; private set; }

    public ReaderSettingsWindow(ReaderSettings current)
    {
        InitializeComponent();
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
        _isInitializing = false;
        UpdateValueLabels();
        UpdateDependentEnabledState();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
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
            PositionDisplayMode = int.TryParse(Selected(PositionDisplayPicker, "0"), out var positionMode) ? positionMode : 0
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

    private void ControlChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            UpdateDependentEnabledState();
        }
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueLabels();
    private void FontWeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueLabels();
    private void LineHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueLabels();
    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueLabels();
    private void AutoScrollSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueLabels();
    private void RulerLinesSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateValueLabels();

    private void UpdateValueLabels()
    {
        if (FontSizeValue is null)
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
