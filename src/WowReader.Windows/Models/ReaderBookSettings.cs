namespace WowReader.Windows.Models;

public sealed class ReaderBookSettings
{
    public int FontSizePercent { get; set; } = 100;
    public int FontWeight { get; set; } = 400;
    public int LineHeightPercent { get; set; } = 160;
    public int MarginPercent { get; set; } = 5;
    public string FormattingMode { get; set; } = "original";
    public string TextAlignment { get; set; } = "original";
    public bool AutoSpacing { get; set; } = true;

    public static ReaderBookSettings From(ReaderSettings settings) => new()
    {
        FontSizePercent = settings.FontSizePercent,
        FontWeight = settings.FontWeight,
        LineHeightPercent = settings.LineHeightPercent,
        MarginPercent = settings.MarginPercent,
        FormattingMode = settings.FormattingMode,
        TextAlignment = settings.TextAlignment,
        AutoSpacing = settings.AutoSpacing
    };

    public ReaderBookSettings Clone() => (ReaderBookSettings)MemberwiseClone();

    public void Normalize()
    {
        FontSizePercent = Math.Clamp(FontSizePercent, 80, 300);
        FontWeight = Math.Clamp((int)Math.Round(FontWeight / 100d) * 100, 200, 800);
        LineHeightPercent = Math.Clamp((int)Math.Round(LineHeightPercent / 10d) * 10, 120, 220);
        MarginPercent = Math.Clamp(MarginPercent, 1, 14);
        FormattingMode = FormattingMode is "original" or "reader" ? FormattingMode : "original";
        TextAlignment = TextAlignment is "original" or "justify" or "left" or "right" ? TextAlignment : "original";
    }

    public void ApplyTo(ReaderSettings settings)
    {
        settings.FontSizePercent = FontSizePercent;
        settings.FontWeight = FontWeight;
        settings.LineHeightPercent = LineHeightPercent;
        settings.MarginPercent = MarginPercent;
        settings.FormattingMode = FormattingMode;
        settings.TextAlignment = TextAlignment;
        settings.AutoSpacing = AutoSpacing;
        settings.Normalize();
    }
}
