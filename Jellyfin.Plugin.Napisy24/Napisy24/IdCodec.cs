using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// URL-safe base64 helpers.
/// </summary>
internal static class Base64Url
{
    public static string Encode(string value)
    {
        return EncodeBytes(Encoding.UTF8.GetBytes(value));
    }

    public static string EncodeBytes(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string Decode(string value)
    {
        return Encoding.UTF8.GetString(DecodeBytes(value));
    }

    public static byte[] DecodeBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// Encodes and decodes the ids returned to Jellyfin. Jellyfin prefixes them with
/// its own provider hash and splits on the first underscore, so the ids may use
/// colons and must not start with an underscore.
/// </summary>
internal static class IdCodec
{
    private const string HashPrefix = "h:";
    private const string WebPrefix = "w:";

    public static string EncodeHash(string fh, long size, string name)
    {
        return HashPrefix + fh + ":" + size.ToString(CultureInfo.InvariantCulture) + ":" + Base64Url.Encode(name);
    }

    public static bool TryDecodeHash(string id, out string fh, out long size, out string name)
    {
        fh = string.Empty;
        size = 0;
        name = string.Empty;

        if (string.IsNullOrEmpty(id) || !id.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id.Substring(HashPrefix.Length).Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (parts[0].Length != 16 || !parts[0].All(Uri.IsHexDigit))
        {
            return false;
        }

        fh = parts[0];

        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size <= 0)
        {
            return false;
        }

        try
        {
            name = Base64Url.Decode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return name.Length > 0 && name.Length <= 512;
    }

    public static string EncodeWeb(int napisId, int cd, string language, IReadOnlyList<TimeSpan> times)
    {
        var timeString = times.Count == 0
            ? "-"
            : string.Join("|", times.Select(t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)));

        return WebPrefix
            + napisId.ToString(CultureInfo.InvariantCulture)
            + ":"
            + cd.ToString(CultureInfo.InvariantCulture)
            + ":"
            + language
            + ":"
            + Base64Url.Encode(timeString);
    }

    public static bool TryDecodeWeb(string id, out int napisId, out int cd, out string language, out IReadOnlyList<TimeSpan> times)
    {
        napisId = 0;
        cd = 0;
        language = "pl";
        times = Array.Empty<TimeSpan>();

        if (string.IsNullOrEmpty(id) || !id.StartsWith(WebPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id.Substring(WebPrefix.Length).Split(':');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out napisId) || napisId <= 0)
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out cd) || cd < 0)
        {
            return false;
        }

        if (!string.Equals(parts[2], "pl", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parts[2], "en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        language = parts[2].ToLowerInvariant();

        try
        {
            var decoded = Base64Url.Decode(parts[3]);
            if (!string.Equals(decoded, "-", StringComparison.Ordinal))
            {
                var list = new List<TimeSpan>();
                foreach (var item in decoded.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (TimeSpan.TryParse(item, CultureInfo.InvariantCulture, out var time))
                    {
                        list.Add(time);
                    }
                }

                times = list;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        return true;
    }
}
