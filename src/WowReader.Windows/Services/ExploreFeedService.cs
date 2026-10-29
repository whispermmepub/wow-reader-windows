using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using HtmlAgilityPack;

namespace WowReader.Windows.Services;

public sealed record ExplorePost(string Title, string Source, string Url, string Excerpt, DateTimeOffset? PublishedAt);

public sealed class ExploreFeedService
{
    private static readonly (string Host, string Label)[] Sources =
    [
        ("whisper1of.blogspot.com", "Whisper of Words"),
        ("thetpaingwrites.blogspot.com", "Thet Paing Writes"),
        ("youthsbookreflections.blogspot.com", "Youths Book Reflections")
    ];
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int PerSourceLimit = 20;
    private const int TotalLimit = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<(IReadOnlyList<ExplorePost> Posts, bool IsCached)> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.ExploreCache);
        var posts = new List<ExplorePost>();
        var anyNetworkSuccess = false;
        foreach (var source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var uri = new Uri($"https://{source.Host}/feeds/posts/default?alt=json&max-results={PerSourceLimit}");
                using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is >= HttpStatusCode.MovedPermanently and <= HttpStatusCode.TemporaryRedirect)
                    throw new HttpRequestException("The feed redirected; redirects are not followed for privacy and source validation.");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                    throw new InvalidDataException("The feed response is larger than the local safety limit.");
                var json = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                var parsed = Parse(json, source.Host, source.Label);
                if (parsed.Count > 0)
                {
                    posts.AddRange(parsed);
                    SaveCache(source.Host, parsed);
                }
                else
                {
                    posts.AddRange(LoadCache(source.Host));
                }
                anyNetworkSuccess = true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidDataException)
            {
                posts.AddRange(LoadCache(source.Host));
            }
        }

        var sorted = posts
            .GroupBy(post => post.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(post => post.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(TotalLimit)
            .ToArray();
        return (sorted, !anyNetworkSuccess);
    }

    public static bool IsApprovedPostUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && Sources.Any(source => string.Equals(uri.Host, source.Host, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxResponseBytes) throw new InvalidDataException("The feed response is larger than the local safety limit.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IReadOnlyList<ExplorePost> Parse(string json, string host, string label)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("feed", out var feed)
            || feed.ValueKind != JsonValueKind.Object
            || !feed.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return Array.Empty<ExplorePost>();

        var posts = new List<ExplorePost>();
        foreach (var entry in entries.EnumerateArray().Take(PerSourceLimit))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var title = NestedText(entry, "title");
            var content = NestedText(entry, "content");
            if (content.Length == 0) content = NestedText(entry, "summary");
            var published = NestedText(entry, "published");
            var url = AlternateUrl(entry, host);
            if (url is null) continue;
            var excerpt = ExtractExcerpt(content, 240);
            DateTimeOffset? date = DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)
                ? parsedDate
                : null;
            posts.Add(new ExplorePost(title.Length == 0 ? "Untitled post" : title, label, url, excerpt, date));
        }
        return posts;
    }

    private static string NestedText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var node)) return "";
        if (node.ValueKind == JsonValueKind.String) return node.GetString()?.Trim() ?? "";
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("$t", out var value) || value.ValueKind != JsonValueKind.String) return "";
        return value.GetString()?.Trim() ?? "";
    }

    private static string? AlternateUrl(JsonElement entry, string host)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("link", out var links) || links.ValueKind != JsonValueKind.Array) return null;
        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object || !link.TryGetProperty("rel", out var relation)
                || relation.ValueKind != JsonValueKind.String || relation.GetString() != "alternate") continue;
            if (!link.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String) return null;
            var value = href.GetString();
            if (!IsApprovedPostUrl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return null;
            return uri.AbsoluteUri;
        }
        return null;
    }

    private static string ExtractExcerpt(string html, int maximum)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html ?? "");
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//iframe|//object|//svg|//img") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();
        var plain = HtmlEntity.DeEntitize(document.DocumentNode.InnerText).Replace('\u00a0', ' ');
        plain = string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return plain.Length <= maximum ? plain : plain[..(maximum - 1)].TrimEnd() + "…";
    }

    private static string CachePath(string host) => Path.Combine(AppPaths.ExploreCache, host + ".json");

    private static void SaveCache(string host, IReadOnlyList<ExplorePost> posts)
    {
        var path = CachePath(host);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(posts, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static IReadOnlyList<ExplorePost> LoadCache(string host)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ExplorePost>>(File.ReadAllText(CachePath(host))) ?? [];
            return loaded.Where(post => post is not null && IsApprovedPostUrl(post.Url) && post.Source == Sources.First(source => source.Host == host).Label)
                .Take(PerSourceLimit).ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<ExplorePost>();
        }
    }
}
