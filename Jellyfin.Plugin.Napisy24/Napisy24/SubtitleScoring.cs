using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// Heuristic scoring used to rank metadata search results and to decide whether
/// an automated download is safe enough.
/// </summary>
internal static class SubtitleScoring
{
    public static int Score(
        WebSubtitle subtitle,
        SubtitleSearchRequest request,
        bool imdbQuery,
        long? mediaSize,
        string? mediaName,
        string? mediaFolder)
    {
        var score = 0;

        if (imdbQuery)
        {
            score += 40;
        }

        if (request.ContentType == VideoContentType.Episode)
        {
            if (subtitle.Season == request.ParentIndexNumber && subtitle.Episode == request.IndexNumber)
            {
                score += 40;
            }
            else
            {
                return 0;
            }
        }
        else
        {
            if (subtitle.Year.HasValue && request.ProductionYear.HasValue
                && Math.Abs(subtitle.Year.Value - request.ProductionYear.Value) <= 1)
            {
                score += 10;
            }

            var titleScore = Jaccard(Tokens(request.Name), Tokens(subtitle.Title));
            if (titleScore >= 0.5)
            {
                score += 15;
            }
            else if (titleScore >= 0.3)
            {
                score += 7;
            }
        }

        if (mediaSize is > 0 && subtitle.TotalSize > 0)
        {
            var difference = Math.Abs(subtitle.TotalSize - mediaSize.Value);
            var tolerance = Math.Max(mediaSize.Value * 0.01, 1024 * 1024);
            if (difference <= tolerance)
            {
                score += 40;
            }
            else if (difference <= mediaSize.Value * 0.05)
            {
                score += 20;
            }
        }

        if (request.RuntimeTicks is > 0 && subtitle.TotalTime > TimeSpan.Zero)
        {
            var runtime = TimeSpan.FromTicks(request.RuntimeTicks.Value);
            var difference = Math.Abs((subtitle.TotalTime - runtime).TotalSeconds);
            if (difference <= 2)
            {
                score += 30;
            }
            else if (difference <= 10)
            {
                score += 15;
            }
            else if (difference <= 60)
            {
                score += 5;
            }
        }

        var releaseScore = subtitle.Releases
            .Select(release => Jaccard(Tokens(mediaName), Tokens(release)))
            .DefaultIfEmpty(0)
            .Max();

        var folderScore = subtitle.Releases
            .Select(release => Jaccard(Tokens(mediaFolder), Tokens(release)))
            .DefaultIfEmpty(0)
            .Max();

        score += (int)Math.Round(Math.Max(releaseScore, folderScore) * 20);

        if (subtitle.Rating is > 0)
        {
            score += (int)Math.Round(subtitle.Rating.Value / 6.0 * 5.0);
        }

        return Math.Clamp(score, 0, 95);
    }

    public static HashSet<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var normalized = RemoveDiacritics(value)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        return new string(normalized)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static double Similarity(string? left, string? right)
    {
        return Jaccard(Tokens(left), Tokens(right));
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
