using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Napisy24.Activation;
using Jellyfin.Plugin.Napisy24.Napisy24;
using Xunit;

namespace Jellyfin.Plugin.Napisy24.Tests;

public class FileHashesTests
{
    [Fact]
    public async Task OshHash_MatchesKnownVector()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[200000];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i * 37 + 11) % 256);
            }

            await File.WriteAllBytesAsync(path, data);

            var hash = await FileHashes.ComputeOshAsync(path, CancellationToken.None);

            Assert.Equal("6020DFA05F22CD40", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NapiprojektHash_MatchesKnownVector()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[200000];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i * 37 + 11) % 256);
            }

            await File.WriteAllBytesAsync(path, data);

            var hash = await FileHashes.ComputeNapiprojektAsync(path, CancellationToken.None);

            Assert.Equal("0dc977606fabb61fbf84cef39d163779", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class IdCodecTests
{
    [Fact]
    public void HashId_RoundTrips()
    {
        var id = IdCodec.EncodeHash("A5E905CBBC002765", 2785089006, "F1 The Movie 2025 1080p.mkv");
        Assert.True(IdCodec.TryDecodeHash(id, out var fh, out var size, out var name));
        Assert.Equal("A5E905CBBC002765", fh);
        Assert.Equal(2785089006, size);
        Assert.Equal("F1 The Movie 2025 1080p.mkv", name);
    }

    [Fact]
    public void WebId_RoundTrips()
    {
        var times = new[] { TimeSpan.FromMinutes(50) + TimeSpan.FromSeconds(54), TimeSpan.FromMinutes(55) + TimeSpan.FromSeconds(10) };
        var id = IdCodec.EncodeWeb(42861, 0, "pl", times);

        Assert.True(IdCodec.TryDecodeWeb(id, out var napisId, out var cd, out var language, out var decoded));
        Assert.Equal(42861, napisId);
        Assert.Equal(0, cd);
        Assert.Equal("pl", language);
        Assert.Equal(times, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("h:xyz:123:abc")]
    [InlineData("w:0:0:pl:LQ")]
    [InlineData("w:1:0:de:LQ")]
    public void InvalidIds_AreRejected(string id)
    {
        Assert.False(IdCodec.TryDecodeHash(id, out _, out _, out _));
        Assert.False(IdCodec.TryDecodeWeb(id, out _, out _, out _, out _));
    }
}

public class SubtitleFormatTests
{
    [Fact]
    public void MicroDvd_IsConvertedToSrt_WithFrameRate()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var microDvd = Encoding.GetEncoding(1250).GetBytes(
            "{51}{90}Podróż dookoła Księżyca|musi przebiegać w dwóch fazach.\r\n"
            + "{91}{122}Rakieta startująca z powierzchni Ziemi\r\n");

        var srt = SubtitleFormat.MicroDvdToSrt(microDvd, 23.976);
        var text = Encoding.UTF8.GetString(srt);

        Assert.StartsWith("1\n00:00:02,127 --> 00:00:03,753\n", text);
        Assert.Contains("Podróż dookoła Księżyca\nmusi przebiegać w dwóch fazach.", text);
        Assert.Contains("2\n00:00:03,795 --> 00:00:05,088", text);
    }

    [Fact]
    public void Srt_IsDetectedAndKeptAsUtf8()
    {
        var srt = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nZażółć gęślą jaźń\n");

        Assert.Equal("srt", SubtitleFormat.Detect(srt));
        Assert.Equal(srt, SubtitleFormat.ToSrt(new SubtitleFile("x.srt", srt, "srt"), null));
    }
}

public class SrtToolsTests
{
    [Fact]
    public void Merge_ShiftsSecondCdByFirstDuration()
    {
        var first = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nA\n");
        var second = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nB\n");

        var merged = SrtTools.Merge(
            new[] { new SubtitleFile("cd1.srt", first, "srt"), new SubtitleFile("cd2.srt", second, "srt") },
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5) });

        Assert.NotNull(merged);
        var text = Encoding.UTF8.GetString(merged!);
        Assert.Contains("1\n00:00:01,000 --> 00:00:02,000\nA", text);
        Assert.Contains("2\n00:00:11,000 --> 00:00:12,000\nB", text);
    }

    [Fact]
    public void Merge_TrimsOverlap()
    {
        var first = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:20,000\nA\n");
        var second = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nB\n");

        var merged = SrtTools.Merge(
            new[] { new SubtitleFile("cd1.srt", first, "srt"), new SubtitleFile("cd2.srt", second, "srt") },
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5) });

        var text = Encoding.UTF8.GetString(merged!);
        Assert.Contains("00:00:01,000 --> 00:00:11,000", text);
        Assert.Contains("00:00:11,000 --> 00:00:13,000", text);
    }
}

public class SubtitleArchiveTests
{
    [Fact]
    public void Extract_KeepsOnlyValidSubtitles()
    {
        var srt = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var zip = CreateZip(
            ("movie.srt", srt),
            ("Napisy24.pl.url", Encoding.ASCII.GetBytes("URL=http://napisy24.pl/")),
            ("junk.txt", Encoding.ASCII.GetBytes("not subtitles at all")),
            ("nativelog.txt", Encoding.ASCII.GetBytes("Set run directory to C:\\Program Files")));

        var files = SubtitleArchive.Extract(zip);

        Assert.Single(files);
        Assert.Equal("movie.srt", files[0].Name);
        Assert.Equal("srt", files[0].Format);
    }

    internal static byte[] CreateZip(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return memory.ToArray();
    }
}

public class Napisy24ParsingTests
{
    [Fact]
    public void WebXml_ParsesMultipleRootsAndFields()
    {
        const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>

<subtitle>
  <id>127240</id>
  <title>The Last of Us 1x01</title>
  <season>1</season>
  <episode>1</episode>
  <epTitle>When You're Lost in the Darkness</epTitle>
  <imdb>tt11957006</imdb>
  <year>2023</year>
  <release>WEBRip.x264-ION10;1080p.WEB.H264-CAKES</release>
  <language>pl</language>
  <cd>1</cd>
  <time>01:20:47|||</time>
  <size>819684409|||</size>
  <fps>23.976</fps>
  <resolution>720x400</resolution>
  <author>Wojtek</author>
  <rating>3.0000</rating>
</subtitle>

<subtitle>
  <id>127277</id>
  <title>The Last of Us 1x02</title>
  <season>1</season>
  <episode>2</episode>
  <language>en</language>
</subtitle>";

        var subtitles = Napisy24Client.ParseWebXml(xml);

        Assert.Equal(2, subtitles.Count);
        Assert.Equal(127240, subtitles[0].Id);
        Assert.Equal(1, subtitles[0].Season);
        Assert.Equal(1, subtitles[0].Episode);
        Assert.Equal("tt11957006", subtitles[0].Imdb);
        Assert.Equal(TimeSpan.FromMinutes(80) + TimeSpan.FromSeconds(47), subtitles[0].TotalTime);
        Assert.Equal(819684409, subtitles[0].TotalSize);
        Assert.Equal(2, subtitles[0].Releases.Count);
        Assert.Equal("en", subtitles[1].Language);
    }

    [Fact]
    public void WebXml_EmptyResponseReturnsNothing()
    {
        Assert.Empty(Napisy24Client.ParseWebXml("brak wynikow"));
    }

    [Fact]
    public void HashResponse_ParsesOk2Archive()
    {
        var srt = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        var zip = SubtitleArchiveTests.CreateZip(("movie.srt", srt));
        var header = Encoding.UTF8.GetBytes(
            "OK-2|res:1920x800|time:02:35:49|fps:23.976|imdb:16311594|napisId:132185|lp:1||");
        var body = header.Concat(zip).ToArray();

        var result = Napisy24Client.ParseHashResponse(body);

        Assert.True(result.Success);
        Assert.Equal("OK-2", result.Status);
        Assert.Equal("132185", result.NapisId);
        Assert.Equal(23.976, result.Fps);
        Assert.Single(result.Files);
        Assert.Equal("movie.srt", result.BestFile!.Name);
    }

    [Fact]
    public void HashResponse_TreatsJunkArchiveAsNoResult()
    {
        var junk = SubtitleArchiveTests.CreateZip(
            ("nativelog.txt", Encoding.ASCII.GetBytes("Set run directory to C:\\Program Files")));
        var header = Encoding.UTF8.GetBytes("OK-2|res:|time:|fps:|imdb:0|napisId:0|lp:1||");
        var body = header.Concat(junk).ToArray();

        var result = Napisy24Client.ParseHashResponse(body);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void HashResponse_DetectsLoginError()
    {
        var result = Napisy24Client.ParseHashResponse(Encoding.UTF8.GetBytes("login error"));

        Assert.True(result.LoginError);
        Assert.False(result.Success);
    }
}

public class ActivationCodeTests
{
    private static string CreateCode(ECDsa key, string serverId, long expires = 0, string name = "test")
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sid = serverId,
            name,
            exp = expires,
        });

        var signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return "N24v1." + Base64Url.EncodeBytes(payload) + "." + Base64Url.EncodeBytes(signature);
    }

    [Fact]
    public void ValidCode_IsAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var code = CreateCode(key, "server-1");

        Assert.True(ActivationCode.TryVerify(code, "server-1", publicKey, out var info, out var error), error);
        Assert.NotNull(info);
        Assert.Equal("server-1", info!.ServerId);
        Assert.Equal("test", info.Name);
    }

    [Fact]
    public void CodeForAnotherServer_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var code = CreateCode(key, "server-1");

        Assert.False(ActivationCode.TryVerify(code, "server-2", publicKey, out _, out var error));
        Assert.Contains("different server", error);
    }

    [Fact]
    public void SharedCode_IsAcceptedOnAnyServer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var code = CreateCode(key, "*");

        Assert.True(ActivationCode.TryVerify(code, "server-1", publicKey, out _, out _));
        Assert.True(ActivationCode.TryVerify(code, "server-2", publicKey, out _, out _));
    }

    [Fact]
    public void ExpiredCode_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var code = CreateCode(key, "server-1", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());

        Assert.False(ActivationCode.TryVerify(code, "server-1", publicKey, out _, out var error));
        Assert.Contains("expired", error);
    }

    [Fact]
    public void TamperedCode_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var code = CreateCode(key, "server-1");
        var parts = code.Split('.');
        var payload = Base64Url.Decode(parts[1]).Replace("server-1", "server-2");

        var tampered = parts[0] + "." + Base64Url.Encode(payload) + "." + parts[2];

        Assert.False(ActivationCode.TryVerify(tampered, "server-1", publicKey, out _, out var error));
        Assert.Contains("signature", error);
    }

    [Fact]
    public void GarbageCode_IsRejected()
    {
        Assert.False(ActivationCode.TryVerify("not-a-code", "server-1", ActivationService.PublicKeyBase64, out _, out _));
        Assert.False(ActivationCode.TryVerify("N24v1.abc", "server-1", ActivationService.PublicKeyBase64, out _, out _));
    }
}
