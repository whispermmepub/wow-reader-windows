using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed record DailyReadingSummary(DateOnly Date, int Seconds, int Sessions, IReadOnlyList<string> BookIds,
    IReadOnlyDictionary<string, int> BookSeconds)
{
    public int Minutes => (int)Math.Round(Seconds / 60d, MidpointRounding.AwayFromZero);
}

public sealed class ReadingActivityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string DateFormat = "yyyy-MM-dd";
    private readonly Dictionary<string, ActivityDay> _days = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        _days.Clear();
        try
        {
            var data = JsonSerializer.Deserialize<ActivityData>(File.ReadAllText(AppPaths.ActivityFile));
            if (data?.Days is null)
            {
                return;
            }

            foreach (var (date, activity) in data.Days)
            {
                if (DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    && activity is not null)
                {
                    activity.Seconds = Math.Max(0, activity.Seconds);
                    activity.Sessions = Math.Max(0, activity.Sessions);
                    activity.BookIds ??= [];
                    activity.BookSeconds ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    activity.BookIds = activity.BookIds
                        .Where(IsSafeBookId)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    activity.BookSeconds = activity.BookSeconds
                        .Where(item => IsSafeBookId(item.Key) && item.Value > 0)
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                    _days[date] = activity;
                }
            }
        }
        catch (FileNotFoundException)
        {
            // A new local install has no activity history yet.
        }
        catch (JsonException)
        {
            // Ignore a malformed local history file; new activity will be saved normally.
        }
    }

    public void Record(DateTimeOffset timestamp, TimeSpan elapsed, string bookId)
    {
        if (!IsSafeBookId(bookId))
        {
            throw new ArgumentException("The book identifier is invalid.", nameof(bookId));
        }

        var seconds = (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
        if (seconds <= 0 || seconds > 120)
        {
            return;
        }

        var localTimestamp = timestamp.ToLocalTime();
        var dateKey = localTimestamp.ToString(DateFormat, CultureInfo.InvariantCulture);
        if (!_days.TryGetValue(dateKey, out var day))
        {
            day = new ActivityDay();
            _days[dateKey] = day;
        }

        if (day.LastRecordedAtUtc is null
            || timestamp - day.LastRecordedAtUtc.Value > TimeSpan.FromMinutes(15))
        {
            day.Sessions++;
        }

        day.Seconds = checked(day.Seconds + seconds);
        day.LastRecordedAtUtc = timestamp.ToUniversalTime();
        if (!day.BookIds.Contains(bookId, StringComparer.Ordinal))
        {
            day.BookIds.Add(bookId);
        }
        day.BookSeconds.TryGetValue(bookId, out var previousBookSeconds);
        day.BookSeconds[bookId] = checked(previousBookSeconds + seconds);
        Save();
    }

    public int GetSeconds(DateOnly date) => _days.TryGetValue(Key(date), out var day) ? day.Seconds : 0;

    public int GetSessionCount(DateOnly date) => _days.TryGetValue(Key(date), out var day) ? day.Sessions : 0;

    public IReadOnlyList<string> GetBookIds(DateOnly date) =>
        _days.TryGetValue(Key(date), out var day) ? day.BookIds.ToArray() : Array.Empty<string>();

    public IReadOnlyList<DailyReadingSummary> GetDays(DateOnly start, int count)
    {
        if (count <= 0)
        {
            return Array.Empty<DailyReadingSummary>();
        }

        return Enumerable.Range(0, count)
            .Select(offset => start.AddDays(offset))
            .Select(date => _days.TryGetValue(Key(date), out var day)
                ? new DailyReadingSummary(date, day.Seconds, day.Sessions, day.BookIds.ToArray(), new Dictionary<string, int>(day.BookSeconds))
                : new DailyReadingSummary(date, 0, 0, Array.Empty<string>(), new Dictionary<string, int>()))
            .ToArray();
    }

    public int TotalSeconds => _days.Values.Sum(day => day.Seconds);

    public int TotalSessions => _days.Values.Sum(day => day.Sessions);

    public int ActiveDays => _days.Values.Count(day => day.Seconds > 0);

    public int LongestStreak
    {
        get
        {
            var dates = _days.Where(item => item.Value.Seconds > 0)
                .Select(item => DateOnly.ParseExact(item.Key, DateFormat, CultureInfo.InvariantCulture))
                .OrderBy(date => date)
                .ToArray();
            var longest = 0;
            var current = 0;
            DateOnly? previous = null;
            foreach (var date in dates)
            {
                current = previous is not null && date == previous.Value.AddDays(1) ? current + 1 : 1;
                longest = Math.Max(longest, current);
                previous = date;
            }
            return longest;
        }
    }

    public IReadOnlyList<BookReadingSummary> BooksByTime(int limit = 10) => _days.Values
        .SelectMany(day => day.BookSeconds)
        .GroupBy(item => item.Key, StringComparer.Ordinal)
        .Select(group => new BookReadingSummary(group.Key, group.Sum(item => item.Value)))
        .OrderByDescending(item => item.Seconds)
        .Take(Math.Clamp(limit, 0, 100))
        .ToArray();

    public int GetBookSeconds(string bookId) => _days.Values
        .SelectMany(day => day.BookSeconds)
        .Where(item => string.Equals(item.Key, bookId, StringComparison.Ordinal))
        .Sum(item => item.Value);

    public int CurrentStreak(DateOnly today)
    {
        var date = GetSeconds(today) > 0 ? today : today.AddDays(-1);
        var streak = 0;
        while (GetSeconds(date) > 0)
        {
            streak++;
            date = date.AddDays(-1);
        }
        return streak;
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.ActivityFile + ".tmp";
        var data = new ActivityData { Days = _days };
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporary, AppPaths.ActivityFile, overwrite: true);
    }

    private static string Key(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static bool IsSafeBookId(string? id) =>
        id is { Length: 64 } && id.All(char.IsAsciiHexDigit);

    private sealed class ActivityData
    {
        public Dictionary<string, ActivityDay> Days { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class ActivityDay
    {
        public int Seconds { get; set; }
        public int Sessions { get; set; }
        public DateTimeOffset? LastRecordedAtUtc { get; set; }
        public List<string> BookIds { get; set; } = [];
        public Dictionary<string, int> BookSeconds { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed record BookReadingSummary(string BookId, int Seconds);
