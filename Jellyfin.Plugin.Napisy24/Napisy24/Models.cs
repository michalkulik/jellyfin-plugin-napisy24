using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// Settings snapshot for a single request.
/// </summary>
internal sealed record Napisy24Options(
    string BaseUrl,
    string AgentUsername,
    string AgentPassword,
    int RequestDelayMs,
    bool MergeMultiCd)
{
    public string NormalizedBaseUrl => BaseUrl.TrimEnd('/');

    public bool UseMd5Fallback { get; init; }
}

/// <summary>
/// One entry returned by libs/webapi.php.
/// </summary>
internal sealed class WebSubtitle
{
    public int Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? EpisodeTitle { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public string? Imdb { get; init; }

    public int? Year { get; init; }

    public IReadOnlyList<string> Releases { get; init; } = Array.Empty<string>();

    public string Language { get; init; } = "pl";

    public int Cd { get; init; } = 1;

    public IReadOnlyList<TimeSpan> Times { get; init; } = Array.Empty<TimeSpan>();

    public IReadOnlyList<long> Sizes { get; init; } = Array.Empty<long>();

    public double? Fps { get; init; }

    public string? Resolution { get; init; }

    public string? Author { get; init; }

    public double? Rating { get; init; }

    public long TotalSize => Sizes.Sum();

    public TimeSpan TotalTime => Times.Aggregate(TimeSpan.Zero, (current, value) => current + value);
}

/// <summary>
/// One subtitle file extracted from a ZIP archive.
/// </summary>
internal sealed record SubtitleFile(string Name, byte[] Data, string Format);

/// <summary>
/// Result of a CheckSubAgent hash lookup.
/// </summary>
internal sealed class HashLookupResult
{
    public bool LoginError { get; init; }

    public bool Success { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public byte[] Archive { get; init; } = Array.Empty<byte>();

    public IReadOnlyList<SubtitleFile> Files { get; init; } = Array.Empty<SubtitleFile>();

    public string? NapisId => Get("napisId");

    public double? Fps
    {
        get
        {
            var value = Get("fps");
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0
                ? fps
                : null;
        }
    }

    public string? Get(string key)
    {
        return Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    public SubtitleFile? BestFile
    {
        get
        {
            if (Files.Count == 0)
            {
                return null;
            }

            return Files
                .OrderBy(f => f.Format == "srt" ? 0 : 1)
                .ThenByDescending(f => f.Data.Length)
                .First();
        }
    }
}

/// <summary>
/// A single subtitle cue.
/// </summary>
internal sealed class SubtitleCue
{
    public SubtitleCue(TimeSpan start, TimeSpan end, string text)
    {
        Start = start;
        End = end;
        Text = text;
    }

    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string Text { get; set; }
}
