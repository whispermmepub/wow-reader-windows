namespace WowReader.Windows.Models;

public sealed record ReadingPosition(int ChapterIndex, double ScrollFraction, DateTimeOffset UpdatedAt)
{
    public static ReadingPosition Start { get; } = new(0, 0d, DateTimeOffset.UtcNow);
}
