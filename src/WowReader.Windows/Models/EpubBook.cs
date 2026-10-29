namespace WowReader.Windows.Models;

public sealed record EpubChapter(string ArchivePath, string Title, string LocalPath, long Weight);

public sealed record EpubBook(
    string Id,
    string Title,
    string SourcePath,
    string ExtractedRoot,
    IReadOnlyList<EpubChapter> Chapters,
    string Author = "")
{
    public int OverallPercent(int chapterIndex, double chapterFraction)
    {
        if (Chapters.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(chapterIndex, 0, Chapters.Count - 1);
        var fraction = Math.Clamp(double.IsFinite(chapterFraction) ? chapterFraction : 0d, 0d, 1d);
        var totalWeight = Chapters.Sum(chapter => Math.Max(1L, chapter.Weight));
        var completedWeight = Chapters.Take(index).Sum(chapter => Math.Max(1L, chapter.Weight));
        var currentWeight = Math.Max(1L, Chapters[index].Weight);
        return (int)Math.Clamp(Math.Round((completedWeight + currentWeight * fraction) * 100d / totalWeight), 0d, 100d);
    }
}
