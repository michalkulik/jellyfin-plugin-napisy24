using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// Subtitle format detection and conversion (SRT / MicroDVD).
/// </summary>
internal static class SubtitleFormat
{
    static SubtitleFormat()
    {
        // Jellyfin registers the code pages provider at startup, but the plugin
        // also runs in unit tests and in tooling; make CP1250 available here.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Regex MicroDvdLine = new(
        @"^\s*[\[{]\s*(-?\d+)\s*[\]}]\s*[\[{]\s*(-?\d+)\s*[\]}]\s*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex SrtTime = new(
        @"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[,.]?(?<f>\d{1,3})?\s*-->\s*(?<eh>\d{1,3}):(?<em>\d{2}):(?<es>\d{2})[,.]?(?<ef>\d{1,3})?",
        RegexOptions.Compiled);

    private static readonly Regex FormattingTag = new(@"\{[^}]*\}", RegexOptions.Compiled);

    public static string DecodeText(byte[] data, out bool isUtf8)
    {
        isUtf8 = false;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            isUtf8 = true;
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(data);
            isUtf8 = true;
            return text;
        }
        catch (DecoderFallbackException)
        {
            // napisy24 legacy files are Windows-1250.
        }

        try
        {
            return Encoding.GetEncoding(1250).GetString(data);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    public static string Detect(byte[] data)
    {
        var text = DecodeText(data, out _);
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        if (trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            return "vtt";
        }

        if (trimmed.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            return "ass";
        }

        if (SrtTime.IsMatch(text))
        {
            return "srt";
        }

        if (MicroDvdLine.IsMatch(trimmed.Split('\n')[0]))
        {
            return "microdvd";
        }

        return "unknown";
    }

    public static byte[] ToSrt(SubtitleFile file, double? fps)
    {
        return file.Format switch
        {
            "srt" => NormalizeSrt(file.Data),
            "microdvd" => MicroDvdToSrt(file.Data, fps),
            _ => throw new InvalidDataException($"Unsupported subtitle format: {file.Format}"),
        };
    }

    public static byte[] NormalizeSrt(byte[] data)
    {
        var text = DecodeText(data, out var isUtf8);
        if (isUtf8)
        {
            return data;
        }

        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] MicroDvdToSrt(byte[] data, double? fps)
    {
        var text = DecodeText(data, out _);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        double? headerFps = null;
        var parsedLines = new List<(int Start, int End, string Text)>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = MicroDvdLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startFrame)
                || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endFrame))
            {
                continue;
            }

            var content = match.Groups[3].Value;

            // MicroDVD sometimes starts with a frame-rate header: {1}{1}23.976
            if (headerFps is null
                && parsedLines.Count == 0
                && startFrame == 1
                && endFrame == 1
                && double.TryParse(content.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps)
                && parsedFps > 1
                && parsedFps < 120)
            {
                headerFps = parsedFps;
                continue;
            }

            if (startFrame < 0 || endFrame <= startFrame)
            {
                continue;
            }

            var textLines = content.Split('|')
                .Select(l => FormattingTag.Replace(l, string.Empty).Trim())
                .Where(l => l.Length > 0)
                .ToArray();

            if (textLines.Length == 0)
            {
                continue;
            }

            parsedLines.Add((startFrame, endFrame, string.Join("\n", textLines)));
        }

        if (parsedLines.Count == 0)
        {
            throw new InvalidDataException("MicroDVD file contains no cues.");
        }

        var effectiveFps = fps ?? headerFps ?? 25.0;
        var cues = parsedLines
            .Select(item => new SubtitleCue(
                TimeSpan.FromSeconds(item.Start / effectiveFps),
                TimeSpan.FromSeconds(item.End / effectiveFps),
                item.Text))
            .ToList();

        return SrtTools.Render(cues);
    }

}

/// <summary>
/// SRT parsing, rendering and multi-CD merging.
/// </summary>
internal static class SrtTools
{
    public static IReadOnlyList<SubtitleCue> Parse(byte[] data)
    {
        var text = SubtitleFormat.DecodeText(data, out _)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        var cues = new List<SubtitleCue>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var timeIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timeIndex < 0)
            {
                continue;
            }

            var match = Regex.Match(
                lines[timeIndex],
                @"(?<sh>\d{1,3}):(?<sm>\d{2}):(?<ss>\d{2})[,.]?(?<sf>\d{1,3})?\s*-->\s*(?<eh>\d{1,3}):(?<em>\d{2}):(?<es>\d{2})[,.]?(?<ef>\d{1,3})?",
                RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                continue;
            }

            var start = new TimeSpan(
                0,
                int.Parse(match.Groups["sh"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["sm"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture),
                ParseMilliseconds(match.Groups["sf"].Value));

            var end = new TimeSpan(
                0,
                int.Parse(match.Groups["eh"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["em"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["es"].Value, CultureInfo.InvariantCulture),
                ParseMilliseconds(match.Groups["ef"].Value));

            var content = string.Join("\n", lines.Skip(timeIndex + 1)).Trim();
            if (content.Length == 0 || end <= start)
            {
                continue;
            }

            cues.Add(new SubtitleCue(start, end, content));
        }

        return cues;
    }

    public static byte[] Render(IReadOnlyList<SubtitleCue> cues)
    {
        var ordered = cues.OrderBy(c => c.Start).ThenBy(c => c.End).ToArray();
        var builder = new StringBuilder();

        for (var i = 0; i < ordered.Length; i++)
        {
            builder.Append(i + 1).Append('\n');
            builder.Append(FormatTime(ordered[i].Start)).Append(" --> ").Append(FormatTime(ordered[i].End)).Append('\n');
            builder.Append(ordered[i].Text.Trim()).Append('\n').Append('\n');
        }

        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    /// <summary>
    /// Merges subtitles split across multiple CDs. Each file is shifted by the
    /// cumulative duration of the previous files and overlapping cues are trimmed.
    /// </summary>
    public static byte[]? Merge(IReadOnlyList<SubtitleFile> files, IReadOnlyList<TimeSpan> durations)
    {
        if (files.Count == 0 || durations.Count < files.Count)
        {
            return null;
        }

        var cues = new List<SubtitleCue>();
        var offset = TimeSpan.Zero;

        for (var i = 0; i < files.Count; i++)
        {
            var part = Parse(SubtitleFormat.ToSrt(files[i], null));
            foreach (var cue in part)
            {
                cues.Add(new SubtitleCue(cue.Start + offset, cue.End + offset, cue.Text));
            }

            offset += durations[i];
        }

        cues.Sort((a, b) => a.Start.CompareTo(b.Start));

        for (var i = 1; i < cues.Count; i++)
        {
            if (cues[i].Start < cues[i - 1].End && cues[i].Start > cues[i - 1].Start)
            {
                cues[i - 1].End = cues[i].Start;
            }

            if (cues[i].End <= cues[i].Start)
            {
                cues.RemoveAt(i);
                i--;
            }
        }

        return Render(cues);
    }

    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            (int)time.TotalHours,
            time.Minutes,
            time.Seconds,
            time.Milliseconds);
    }

    private static int ParseMilliseconds(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        return int.Parse(value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
    }
}
