using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// Extracts and validates subtitle files from a napisy24 ZIP archive.
/// </summary>
internal static class SubtitleArchive
{
    private const int MaxEntrySize = 5 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt",
        ".txt",
        ".sub",
        ".vtt",
        ".ass",
        ".ssa",
    };

    public static IReadOnlyList<SubtitleFile> Extract(byte[] archive)
    {
        if (archive.Length < 4 || archive[0] != (byte)'P' || archive[1] != (byte)'K')
        {
            return Array.Empty<SubtitleFile>();
        }

        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var files = new List<SubtitleFile>();

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var extension = Path.GetExtension(entry.Name);
                if (!AllowedExtensions.Contains(extension))
                {
                    continue;
                }

                if (entry.Length <= 0 || entry.Length > MaxEntrySize)
                {
                    continue;
                }

                using var entryStream = entry.Open();
                using var memory = new MemoryStream((int)Math.Min(entry.Length, MaxEntrySize));
                entryStream.CopyTo(memory);
                var data = memory.ToArray();

                var format = SubtitleFormat.Detect(data);
                if (format == "unknown")
                {
                    continue;
                }

                files.Add(new SubtitleFile(entry.Name, data, format));
            }

            return files;
        }
        catch (InvalidDataException)
        {
            return Array.Empty<SubtitleFile>();
        }
    }

    public static bool IsValid(IReadOnlyList<SubtitleFile> files)
    {
        return files.Any(file => file.Format is "srt" or "microdvd");
    }
}
