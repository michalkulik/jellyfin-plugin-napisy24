using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Napisy24.Napisy24;

/// <summary>
/// File hashes used by napisy24.
/// </summary>
internal static class FileHashes
{
    private const int ChunkSize = 64 * 1024;
    private const int NapiprojektChunkSize = 10 * 1024 * 1024;

    /// <summary>
    /// Computes the OpenSubtitles/MPC hash (napisy24 field <c>fh</c>):
    /// file size plus the sum of the first and last 64 KiB read as little-endian
    /// unsigned 64-bit words.
    /// </summary>
    public static async Task<string?> ComputeOshAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception)
        {
            return null;
        }

        if (!info.Exists || info.Length < ChunkSize * 2)
        {
            return null;
        }

        ulong hash = (ulong)info.Length;
        var buffer = new byte[ChunkSize];

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var read = await ReadFullAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        hash += SumWords(buffer.AsSpan(0, read));

        stream.Seek(-ChunkSize, SeekOrigin.End);
        read = await ReadFullAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        hash += SumWords(buffer.AsSpan(0, read));

        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Computes the napiprojekt hash (napisy24 field <c>md</c>): MD5 of the
    /// first 10 MiB of the file.
    /// </summary>
    public static async Task<string?> ComputeNapiprojektAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception)
        {
            return null;
        }

        if (!info.Exists || info.Length == 0)
        {
            return null;
        }

        var buffer = new byte[(int)Math.Min(info.Length, NapiprojektChunkSize)];

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var hash = MD5.HashData(buffer.AsSpan(0, total));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static ulong SumWords(ReadOnlySpan<byte> data)
    {
        ulong sum = 0;
        var words = data.Length / sizeof(ulong);
        for (var i = 0; i < words; i++)
        {
            sum += BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * sizeof(ulong), sizeof(ulong)));
        }

        return sum;
    }
}
