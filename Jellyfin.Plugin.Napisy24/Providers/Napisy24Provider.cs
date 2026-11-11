using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Napisy24.Activation;
using Jellyfin.Plugin.Napisy24.Configuration;
using Jellyfin.Plugin.Napisy24.Napisy24;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Napisy24.Providers;

/// <summary>
/// Subtitle provider for napisy24.pl.
/// </summary>
public class Napisy24Provider : ISubtitleProvider, IHasOrder
{
    /// <summary>
    /// Name of the named HTTP client registered by the plugin.
    /// </summary>
    public const string HttpClientName = "Napisy24";

    private static readonly ConcurrentDictionary<string, CacheEntry> HashCache = new(StringComparer.Ordinal);
    private readonly ILogger<Napisy24Provider> _logger;
    private readonly ILocalizationManager _localizationManager;
    private readonly ActivationService _activation;
    private readonly Napisy24Client _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="Napisy24Provider"/> class.
    /// </summary>
    public Napisy24Provider(
        ILogger<Napisy24Provider> logger,
        IHttpClientFactory httpClientFactory,
        ILocalizationManager localizationManager,
        ActivationService activation)
    {
        _logger = logger;
        _localizationManager = localizationManager;
        _activation = activation;
        _client = new Napisy24Client(httpClientFactory, logger);
    }

    /// <inheritdoc />
    public string Name => "Napisy24";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

    /// <inheritdoc />
    public int Order => 2;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_activation.GetStatus().IsActivated)
        {
            _logger.LogDebug("Napisy24 is not activated; ignoring the search request");
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var language = ResolveLanguage(request);
        if (language is null)
        {
            _logger.LogDebug("Napisy24 does not support requested language {Language}", request.Language);
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var config = GetConfiguration();
        var options = CreateOptions(config);
        var media = GetMediaInfo(request.MediaPath);
        var results = new List<ScoredResult>();

        if (config.EnableHashLookup && language == "pl" && media is not null)
        {
            try
            {
                var hashResult = await SearchByHashAsync(request, options, media, cancellationToken).ConfigureAwait(false);
                if (hashResult is not null)
                {
                    results.Add(hashResult);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Napisy24 hash lookup failed for {Path}", request.MediaPath);
            }
        }

        if (request.IsPerfectMatch)
        {
            return results.Select(result => result.Info).ToArray();
        }

        var metadataAllowed = config.EnableMetadataSearch
            && (!request.IsAutomated || config.EnableAutomaticMetadataSearch);

        if (metadataAllowed)
        {
            try
            {
                var webResults = await SearchMetadataAsync(request, options, language, media, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var (subtitle, imdbQuery) in webResults)
                {
                    var score = SubtitleScoring.Score(
                        subtitle,
                        request,
                        imdbQuery,
                        media?.Size,
                        media?.Name,
                        media?.Folder);

                    if (request.IsAutomated && score < config.AutoScoreThreshold)
                    {
                        continue;
                    }

                    results.Add(new ScoredResult(BuildWebInfo(subtitle, language), score, false));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Napisy24 metadata search failed for {Name}", request.Name);
            }
        }

        return results
            .GroupBy(result => result.Info.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(result => result.IsHashMatch)
            .ThenByDescending(result => result.Score)
            .ThenByDescending(result => result.Info.CommunityRating ?? 0)
            .Take(Math.Max(1, config.MaxResults))
            .Select(result => result.Info)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Subtitle id is empty.", nameof(id));
        }

        if (!_activation.GetStatus().IsActivated)
        {
            throw new InvalidOperationException(
                "Napisy24 is not activated. Enter an activation code in Dashboard -> Plugins -> Napisy24.");
        }

        var config = GetConfiguration();
        var options = CreateOptions(config);

        if (IdCodec.TryDecodeHash(id, out var fh, out var size, out var name))
        {
            var lookup = await GetCachedOrLookupAsync(options, id, fh, size, name, cancellationToken).ConfigureAwait(false);
            var file = lookup.BestFile
                ?? throw new InvalidOperationException("napisy24 returned no subtitle file for this hash.");

            var srt = SubtitleFormat.ToSrt(file, lookup.Fps);
            return new SubtitleResponse
            {
                Format = "srt",
                Language = "pl",
                Stream = new MemoryStream(srt),
            };
        }

        if (IdCodec.TryDecodeWeb(id, out var napisId, out var cd, out var language, out var times))
        {
            var archive = await _client.DownloadByIdAsync(options, napisId, cancellationToken).ConfigureAwait(false);
            var files = SubtitleArchive.Extract(archive);
            if (!SubtitleArchive.IsValid(files))
            {
                throw new InvalidOperationException($"napisy24 archive for napisId {napisId} contains no usable subtitle.");
            }

            var selected = SelectWebFiles(files, cd, times, config.MergeMultiCd);
            var srt = selected.Count == 1
                ? SubtitleFormat.ToSrt(selected[0], null)
                : SrtTools.Merge(selected, times)
                    ?? SubtitleFormat.ToSrt(selected.OrderByDescending(file => file.Data.Length).First(), null);

            return new SubtitleResponse
            {
                Format = "srt",
                Language = language,
                Stream = new MemoryStream(srt),
            };
        }

        throw new ArgumentException("Unknown subtitle id format.", nameof(id));
    }

    private async Task<ScoredResult?> SearchByHashAsync(
        SubtitleSearchRequest request,
        Napisy24Options options,
        MediaInfo media,
        CancellationToken cancellationToken)
    {
        var fh = await FileHashes.ComputeOshAsync(request.MediaPath, cancellationToken).ConfigureAwait(false);
        if (fh is null)
        {
            return null;
        }

        var lookup = await _client.LookupByHashAsync(options, fh, media.Size, media.Name, null, cancellationToken)
            .ConfigureAwait(false);

        if (lookup.LoginError)
        {
            _logger.LogWarning("napisy24 rejected the configured agent credentials");
            return null;
        }

        if (!lookup.Success && options.UseMd5Fallback)
        {
            var md5 = await FileHashes.ComputeNapiprojektAsync(request.MediaPath, cancellationToken).ConfigureAwait(false);
            if (md5 is not null)
            {
                lookup = await _client.LookupByHashAsync(options, fh, media.Size, media.Name, md5, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (!lookup.Success)
        {
            _logger.LogDebug("Napisy24 hash lookup for {Path} returned {Status}", request.MediaPath, lookup.Status);
            return null;
        }

        var id = IdCodec.EncodeHash(fh, media.Size, media.Name);
        Cache(lookup, id);

        return new ScoredResult(BuildHashInfo(lookup, id, media.Name), 100, true);
    }

    private async Task<IReadOnlyList<(WebSubtitle Subtitle, bool ImdbQuery)>> SearchMetadataAsync(
        SubtitleSearchRequest request,
        Napisy24Options options,
        string language,
        MediaInfo? media,
        CancellationToken cancellationToken)
    {
        var imdb = GetImdbId(request);
        var results = new List<(WebSubtitle, bool)>();

        if (request.ContentType == VideoContentType.Episode)
        {
            if (!request.ParentIndexNumber.HasValue
                || !request.IndexNumber.HasValue
                || string.IsNullOrWhiteSpace(request.SeriesName))
            {
                return results;
            }

            if (imdb is not null)
            {
                var byImdb = await _client.SearchWebAsync(options, "imdb", imdb, language, cancellationToken)
                    .ConfigureAwait(false);

                results.AddRange(FilterEpisodes(byImdb, request)
                    .Where(subtitle => MatchesLanguage(subtitle, language))
                    .Select(subtitle => (subtitle, true)));
                if (results.Count > 0)
                {
                    return results;
                }
            }

            foreach (var seriesName in SeriesNameVariants(request.SeriesName))
            {
                var query = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1}x{2:00}",
                    seriesName,
                    request.ParentIndexNumber.Value,
                    request.IndexNumber.Value);

                var episodes = await _client.SearchWebAsync(options, "title", query, language, cancellationToken)
                    .ConfigureAwait(false);

                results.AddRange(FilterEpisodes(episodes, request)
                    .Where(subtitle => MatchesLanguage(subtitle, language))
                    .Select(subtitle => (subtitle, false)));
                if (results.Count > 0)
                {
                    return results;
                }
            }

            var series = await _client.SearchWebAsync(options, "title", request.SeriesName, language, cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(FilterEpisodes(series, request)
                .Where(subtitle => MatchesLanguage(subtitle, language))
                .Select(subtitle => (subtitle, false)));
            return results;
        }

        if (imdb is not null)
        {
            var byImdb = await _client.SearchWebAsync(options, "imdb", imdb, language, cancellationToken)
                .ConfigureAwait(false);

            results.AddRange(FilterMovies(byImdb, request, media)
                .Where(subtitle => MatchesLanguage(subtitle, language))
                .Select(subtitle => (subtitle, true)));
            if (results.Count > 0)
            {
                return results;
            }
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return results;
        }

        var byTitle = await _client.SearchWebAsync(options, "title", request.Name, language, cancellationToken)
            .ConfigureAwait(false);

        results.AddRange(FilterMovies(byTitle, request, media)
            .Where(subtitle => MatchesLanguage(subtitle, language))
            .Select(subtitle => (subtitle, false)));
        return results;
    }

    private static bool MatchesLanguage(WebSubtitle subtitle, string language)
    {
        return string.Equals(subtitle.Language, language, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<WebSubtitle> FilterEpisodes(IEnumerable<WebSubtitle> subtitles, SubtitleSearchRequest request)
    {
        var first = request.IndexNumber;
        var last = request.IndexNumberEnd ?? first;

        return subtitles.Where(subtitle =>
            subtitle.Season == request.ParentIndexNumber
            && subtitle.Episode >= first
            && subtitle.Episode <= last);
    }

    private static IEnumerable<WebSubtitle> FilterMovies(
        IEnumerable<WebSubtitle> subtitles,
        SubtitleSearchRequest request,
        MediaInfo? media)
    {
        foreach (var subtitle in subtitles)
        {
            if (subtitle.Year.HasValue
                && request.ProductionYear.HasValue
                && Math.Abs(subtitle.Year.Value - request.ProductionYear.Value) > 1)
            {
                continue;
            }

            var titleSimilarity = SubtitleScoring.Similarity(subtitle.Title, request.Name);
            var sizeMatches = false;
            if (media?.Size > 0 && subtitle.TotalSize > 0)
            {
                var difference = Math.Abs(subtitle.TotalSize - media.Size);
                sizeMatches = difference <= Math.Max(media.Size * 0.01, 1024 * 1024);
            }

            if (!subtitle.Year.HasValue && request.ProductionYear.HasValue
                && titleSimilarity < 0.2 && !sizeMatches)
            {
                continue;
            }

            yield return subtitle;
        }
    }

    private static IEnumerable<string> SeriesNameVariants(string seriesName)
    {
        var variants = new List<string> { seriesName };

        var withoutYear = System.Text.RegularExpressions.Regex.Replace(seriesName, @"\s*\(\d{4}\)\s*$", string.Empty).Trim();
        if (!string.Equals(withoutYear, seriesName, StringComparison.Ordinal))
        {
            variants.Add(withoutYear);
        }

        if (seriesName.Contains('&', StringComparison.Ordinal))
        {
            variants.Add(seriesName.Replace("&", "and", StringComparison.Ordinal));
        }

        return variants.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static RemoteSubtitleInfo BuildHashInfo(HashLookupResult lookup, string id, string mediaName)
    {
        var file = lookup.BestFile;
        var name = file is null ? lookup.Get("ftitle") : Path.GetFileNameWithoutExtension(file.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(mediaName);
        }

        var parts = new List<string> { "hash match" };
        AddPart(parts, "res", lookup.Get("res"));
        AddPart(parts, "time", lookup.Get("time"));
        AddPart(parts, "fps", lookup.Get("fps"));
        AddPart(parts, "translator", lookup.Get("ftlumacz"));
        AddPart(parts, "uploader", lookup.Get("fwstawil"));
        AddPart(parts, "proofreader", lookup.Get("fkorekta"));

        return new RemoteSubtitleInfo
        {
            Id = id,
            ProviderName = "Napisy24",
            Name = name,
            Format = "srt",
            Author = lookup.Get("ftlumacz") ?? lookup.Get("fwstawil"),
            Comment = string.Join("; ", parts),
            ThreeLetterISOLanguageName = "pol",
            FrameRate = (float?)lookup.Fps,
            IsHashMatch = true,
            Forced = false,
            HearingImpaired = false,
        };
    }

    private static RemoteSubtitleInfo BuildWebInfo(WebSubtitle subtitle, string language)
    {
        var name = subtitle.Title;
        if (!string.IsNullOrWhiteSpace(subtitle.EpisodeTitle))
        {
            name += " — " + subtitle.EpisodeTitle;
        }
        else if (subtitle.Releases.Count > 0)
        {
            name += " — " + subtitle.Releases[0];
        }

        if (name.Length > 180)
        {
            name = name[..177] + "...";
        }

        var parts = new List<string>();
        if (subtitle.Cd > 1)
        {
            parts.Add($"CD {subtitle.Cd}");
        }

        if (subtitle.Releases.Count > 0)
        {
            parts.Add(string.Join(" | ", subtitle.Releases.Take(3)));
        }

        if (subtitle.TotalTime > TimeSpan.Zero)
        {
            parts.Add("time " + subtitle.TotalTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        }

        if (subtitle.TotalSize > 0)
        {
            parts.Add($"size {subtitle.TotalSize / (1024.0 * 1024.0):0.0} MB");
        }

        if (subtitle.Fps is > 0)
        {
            parts.Add("fps " + subtitle.Fps.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(subtitle.Resolution))
        {
            parts.Add(subtitle.Resolution);
        }

        if (!string.IsNullOrWhiteSpace(subtitle.Author))
        {
            parts.Add("author " + subtitle.Author);
        }

        return new RemoteSubtitleInfo
        {
            Id = IdCodec.EncodeWeb(subtitle.Id, 0, subtitle.Language, subtitle.Times),
            ProviderName = "Napisy24",
            Name = name,
            Format = "srt",
            Author = subtitle.Author,
            Comment = string.Join("; ", parts),
            ThreeLetterISOLanguageName = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "eng" : "pol",
            CommunityRating = subtitle.Rating is > 0 ? (float)(subtitle.Rating.Value * 10.0 / 6.0) : null,
            FrameRate = (float?)subtitle.Fps,
            IsHashMatch = false,
            Forced = false,
            HearingImpaired = false,
        };
    }

    private static void AddPart(ICollection<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "0")
        {
            parts.Add(label + " " + value);
        }
    }

    private static List<SubtitleFile> SelectWebFiles(
        IReadOnlyList<SubtitleFile> files,
        int cd,
        IReadOnlyList<TimeSpan> times,
        bool mergeMultiCd)
    {
        if (cd > 0 && cd <= files.Count)
        {
            return new List<SubtitleFile> { files[cd - 1] };
        }

        if (files.Count == 1)
        {
            return new List<SubtitleFile> { files[0] };
        }

        if (mergeMultiCd && times.Count >= files.Count)
        {
            return files.ToList();
        }

        return new List<SubtitleFile> { files.OrderByDescending(file => file.Data.Length).First() };
    }

    private async Task<HashLookupResult> GetCachedOrLookupAsync(
        Napisy24Options options,
        string id,
        string fh,
        long size,
        string name,
        CancellationToken cancellationToken)
    {
        if (HashCache.TryGetValue(id, out var entry) && entry.Expires > DateTime.UtcNow)
        {
            return entry.Lookup;
        }

        var lookup = await _client.LookupByHashAsync(options, fh, size, name, null, cancellationToken)
            .ConfigureAwait(false);

        if (!lookup.Success)
        {
            throw new InvalidOperationException($"napisy24 returned no subtitles for hash {fh} (status {lookup.Status}).");
        }

        Cache(lookup, id);
        return lookup;
    }

    private static void Cache(HashLookupResult lookup, string id)
    {
        if (HashCache.Count > 50)
        {
            foreach (var pair in HashCache.Where(pair => pair.Value.Expires <= DateTime.UtcNow).ToArray())
            {
                HashCache.TryRemove(pair.Key, out _);
            }
        }

        HashCache[id] = new CacheEntry(DateTime.UtcNow.AddMinutes(10), lookup);
    }

    private static MediaInfo? GetMediaInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return new MediaInfo(info.Length, info.Name, info.Directory?.Name ?? string.Empty);
            }
        }
        catch (Exception)
        {
            // Network path or unreadable file: fall back to the path components.
        }

        return new MediaInfo(
            0,
            Path.GetFileName(path),
            Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty));
    }

    private static string? GetImdbId(SubtitleSearchRequest request)
    {
        if (request.ProviderIds is null
            || !request.ProviderIds.TryGetValue("Imdb", out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return value.All(char.IsDigit) && value.Length > 0 ? "tt" + value : null;
    }

    private string? ResolveLanguage(SubtitleSearchRequest request)
    {
        var raw = request.TwoLetterISOLanguageName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = request.Language;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var info = _localizationManager.FindLanguageInfo(raw);
        var twoLetter = info?.TwoLetterISOLanguageName ?? raw;
        twoLetter = twoLetter.ToLowerInvariant();

        return twoLetter is "pl" or "en" ? twoLetter : null;
    }

    private static PluginConfiguration GetConfiguration()
    {
        return Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    private static Napisy24Options CreateOptions(PluginConfiguration config)
    {
        return new Napisy24Options(
            string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://napisy24.pl" : config.BaseUrl.Trim(),
            config.AgentUsername ?? string.Empty,
            config.AgentPassword ?? string.Empty,
            Math.Clamp(config.RequestDelayMs, 0, 5000),
            config.MergeMultiCd)
        {
            UseMd5Fallback = config.UseMd5Fallback,
        };
    }

    private sealed record ScoredResult(RemoteSubtitleInfo Info, int Score, bool IsHashMatch);

    private sealed record MediaInfo(long Size, string Name, string Folder);

    private sealed record CacheEntry(DateTime Expires, HashLookupResult Lookup);
}
