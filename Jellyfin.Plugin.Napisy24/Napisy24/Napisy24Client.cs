using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// HTTP client for napisy24.pl.
/// </summary>
internal sealed class Napisy24Client
{
    private const string UserAgent = "Jellyfin.Plugin.Napisy24/1.0";
    private const int MaxResponseBytes = 12 * 1024 * 1024;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public Napisy24Client(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClient = httpClientFactory.CreateClient(Providers.Napisy24Provider.HttpClientName);
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
        _logger = logger;
    }

    public async Task<HashLookupResult> LookupByHashAsync(
        Napisy24Options options,
        string fh,
        long size,
        string fileName,
        string? md5,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["postAction"] = "CheckSub",
            ["ua"] = options.AgentUsername,
            ["ap"] = options.AgentPassword,
            ["fh"] = fh,
            ["fs"] = size.ToString(CultureInfo.InvariantCulture),
            ["fn"] = fileName,
            ["nl"] = "PL",
            ["n24pref"] = "1",
        };

        if (!string.IsNullOrWhiteSpace(md5))
        {
            form["md"] = md5;
        }

        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(options, "/run/CheckSubAgent.php"))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await SendAsync(request, options, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode >= HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("napisy24 hash lookup returned HTTP {StatusCode}", (int)response.StatusCode);
            return new HashLookupResult { Status = "http-" + (int)response.StatusCode };
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseHashResponse(body);
    }

    public async Task<IReadOnlyList<WebSubtitle>> SearchWebAsync(
        Napisy24Options options,
        string queryKey,
        string queryValue,
        string language,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(
            options,
            "/libs/webapi.php?"
            + queryKey
            + "="
            + Uri.EscapeDataString(queryValue)
            + "&lang="
            + Uri.EscapeDataString(language));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/xml,application/xml,text/html");

        using var response = await SendAsync(request, options, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode >= HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("napisy24 webapi returned HTTP {StatusCode}", (int)response.StatusCode);
            return Array.Empty<WebSubtitle>();
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseWebXml(Encoding.UTF8.GetString(body));
    }

    public async Task<byte[]> DownloadByIdAsync(
        Napisy24Options options,
        int napisId,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(
            options,
            "/run/pages/download.php?napisId="
            + napisId.ToString(CultureInfo.InvariantCulture)
            + "&typ=sru");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", options.NormalizedBaseUrl + "/");

        using var response = await SendAsync(request, options, cancellationToken).ConfigureAwait(false);

        // Without a Referer napisy24 answers 302 but still sends the ZIP body.
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Found)
        {
            throw new HttpRequestException(
                $"napisy24 download returned HTTP {(int)response.StatusCode} for napisId {napisId}.");
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (body.Length < 4 || body[0] != (byte)'P' || body[1] != (byte)'K')
        {
            throw new InvalidDataException($"napisy24 returned no ZIP for napisId {napisId}.");
        }

        return body;
    }

    private static string BuildUrl(Napisy24Options options, string relative)
    {
        return options.NormalizedBaseUrl + relative;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Napisy24Options options,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            var delay = Math.Max(0, options.RequestDelayMs - elapsed);
            if (delay > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
            }

            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;
            return response;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("napisy24 response is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("napisy24 response is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    internal static HashLookupResult ParseHashResponse(byte[] body)
    {
        var separator = -1;
        for (var i = 0; i + 1 < body.Length; i++)
        {
            if (body[i] == (byte)'|' && body[i + 1] == (byte)'|')
            {
                separator = i;
                break;
            }
        }

        var headerLength = separator >= 0 ? separator : body.Length;
        var header = Encoding.UTF8.GetString(body, 0, headerLength);
        var archive = separator >= 0 ? body[(separator + 2)..] : Array.Empty<byte>();

        if (header.StartsWith("login error", StringComparison.OrdinalIgnoreCase))
        {
            return new HashLookupResult { LoginError = true, Status = "login error" };
        }

        var parts = header.Split('|');
        var status = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts.Skip(1))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            fields[part[..colon]] = part[(colon + 1)..];
        }

        var files = SubtitleArchive.Extract(archive);
        var success = string.Equals(status, "OK-2", StringComparison.OrdinalIgnoreCase)
            && SubtitleArchive.IsValid(files);

        return new HashLookupResult
        {
            Success = success,
            Status = status,
            Fields = fields,
            Archive = archive,
            Files = files,
        };
    }

    internal static IReadOnlyList<WebSubtitle> ParseWebXml(string raw)
    {
        var text = raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text.Length == 0 || text.StartsWith("brak", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<WebSubtitle>();
        }

        if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var end = text.IndexOf("?>", StringComparison.Ordinal);
            if (end >= 0)
            {
                text = text[(end + 2)..];
            }
        }

        // webapi.php emits a sequence of <subtitle> elements without a common root.
        var document = XDocument.Parse("<root>" + text + "</root>");
        var results = new List<WebSubtitle>();

        foreach (var element in document.Root!.Elements().Where(e => e.Name.LocalName == "subtitle"))
        {
            var id = ParseInt(GetValue(element, "id"));
            if (id is null or <= 0)
            {
                continue;
            }

            var releases = (GetValue(element, "release") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value.Length > 0)
                .ToArray();

            var times = ParseTimes(GetValue(element, "time"));
            var sizes = ParseSizes(GetValue(element, "size"));

            results.Add(new WebSubtitle
            {
                Id = id.Value,
                Title = GetValue(element, "title") ?? string.Empty,
                EpisodeTitle = GetValue(element, "epTitle"),
                Season = ParseInt(GetValue(element, "season")),
                Episode = ParseInt(GetValue(element, "episode")),
                Imdb = NormalizeImdb(GetValue(element, "imdb")),
                Year = ParseInt(GetValue(element, "year")),
                Releases = releases,
                Language = (GetValue(element, "language") ?? "pl").ToLowerInvariant(),
                Cd = ParseInt(GetValue(element, "cd")) ?? 1,
                Times = times,
                Sizes = sizes,
                Fps = ParseDouble(GetValue(element, "fps")),
                Resolution = GetValue(element, "resolution"),
                Author = GetValue(element, "author"),
                Rating = ParseDouble(GetValue(element, "rating")),
            });
        }

        return results;
    }

    private static string? GetValue(XElement element, string name)
    {
        var value = element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static IReadOnlyList<TimeSpan> ParseTimes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<TimeSpan>();
        }

        var times = new List<TimeSpan>();
        foreach (var item in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TimeSpan.TryParse(item, CultureInfo.InvariantCulture, out var time))
            {
                times.Add(time);
            }
        }

        return times;
    }

    private static IReadOnlyList<long> ParseSizes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<long>();
        }

        var sizes = new List<long>();
        foreach (var item in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                sizes.Add(size);
            }
        }

        return sizes;
    }

    private static string? NormalizeImdb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = value.Trim();
        if (digits.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return digits.All(char.IsDigit) && digits.Length > 0 ? "tt" + digits : null;
    }
}
