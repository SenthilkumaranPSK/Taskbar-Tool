using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarMediaWidget.Core;

namespace TaskbarMediaWidget.Lyrics;

public sealed record SyncedLyricLine(TimeSpan Timestamp, string Text);

public sealed record LyricsData(bool IsSynced, IReadOnlyList<SyncedLyricLine> SyncedLines, string PlainLyrics);

/// <summary>
/// Fetches free synchronized and plain lyrics from the LRCLIB open API (lrclib.net).
/// </summary>
internal sealed class LrcLibService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
        DefaultRequestHeaders = { { "User-Agent", "TaskbarTool/1.1 (https://github.com/TaskbarTool)" } }
    };

    private static readonly Regex LrcLineRegex = new(@"^\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]\s*(.*)$", RegexOptions.Compiled);

    private readonly Dictionary<string, LyricsData?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public async Task<LyricsData?> GetLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var key = $"{title.Trim()}|{artist?.Trim() ?? string.Empty}";
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrWhiteSpace(artist))
            {
                url += $"&artist_name={Uri.EscapeDataString(artist)}";
            }
            if (duration.TotalSeconds > 10)
            {
                url += $"&duration={(int)duration.TotalSeconds}";
            }

            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                lock (_lock) { _cache[key] = null; }
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? synced = root.TryGetProperty("syncedLyrics", out var synProp) && synProp.ValueKind == JsonValueKind.String
                ? synProp.GetString()
                : null;

            string? plain = root.TryGetProperty("plainLyrics", out var plProp) && plProp.ValueKind == JsonValueKind.String
                ? plProp.GetString()
                : null;

            LyricsData? result = null;

            if (!string.IsNullOrWhiteSpace(synced))
            {
                var lines = ParseLrc(synced);
                if (lines.Count > 0)
                {
                    result = new LyricsData(IsSynced: true, SyncedLines: lines, PlainLyrics: plain ?? string.Empty);
                }
            }

            if (result is null && !string.IsNullOrWhiteSpace(plain))
            {
                result = new LyricsData(IsSynced: false, SyncedLines: Array.Empty<SyncedLyricLine>(), PlainLyrics: plain);
            }

            lock (_lock)
            {
                _cache[key] = result;
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to fetch lyrics for '{title}': {ex.Message}");
            return null;
        }
    }

    private static List<SyncedLyricLine> ParseLrc(string lrcContent)
    {
        var result = new List<SyncedLyricLine>();
        var lines = lrcContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var match = LrcLineRegex.Match(rawLine.Trim());
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, out var minutes) ||
                !int.TryParse(match.Groups[2].Value, out var seconds))
            {
                continue;
            }

            var millis = 0;
            if (match.Groups[3].Success)
            {
                var fracStr = match.Groups[3].Value.PadRight(3, '0')[..3];
                int.TryParse(fracStr, out millis);
            }

            var timestamp = new TimeSpan(0, 0, minutes, seconds, millis);
            var text = match.Groups[4].Value.Trim();

            if (!string.IsNullOrEmpty(text))
            {
                result.Add(new SyncedLyricLine(timestamp, text));
            }
        }

        result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return result;
    }
}
