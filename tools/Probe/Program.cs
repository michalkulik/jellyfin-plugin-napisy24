using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.Napisy24.Napisy24;
using Microsoft.Extensions.Logging.Abstractions;

var factory = new SimpleHttpClientFactory();
var client = new Napisy24Client(factory, NullLogger.Instance);
var options = new Napisy24Options("https://napisy24.pl", "subliminal", "lanimilbus", 500, true);

Console.WriteLine("== webapi title=8 Mila ==");
var movies = await client.SearchWebAsync(options, "title", "8 Mila", "pl", CancellationToken.None);
foreach (var movie in movies.Take(5))
{
    Console.WriteLine($"{movie.Id} | {movie.Title} | {movie.Year} | {movie.Language} | cd={movie.Cd} | {movie.TotalSize} B | {movie.TotalTime}");
}

if (movies.Count > 0)
{
    Console.WriteLine("== download " + movies[0].Id + " ==");
    var archive = await client.DownloadByIdAsync(options, movies[0].Id, CancellationToken.None);
    Console.WriteLine($"zip bytes: {archive.Length}");

    var files = SubtitleArchive.Extract(archive);
    foreach (var file in files)
    {
        Console.WriteLine($"{file.Name} | {file.Format} | {file.Data.Length}");
    }

    if (files.Count > 0)
    {
        var srt = SubtitleFormat.ToSrt(files[0], null);
        Console.WriteLine(Encoding.UTF8.GetString(srt, 0, Math.Min(300, srt.Length)));
    }
}

Console.WriteLine("== hash lookup with a synthetic hash ==");
var hash = await client.LookupByHashAsync(options, "0000000000000000", 123456789, "sample.mkv", null, CancellationToken.None);
Console.WriteLine($"status={hash.Status} loginError={hash.LoginError} files={hash.Files.Count}");

Console.WriteLine("== episode search ==");
var episodes = await client.SearchWebAsync(options, "title", "Przyjaciele 1x01", "pl", CancellationToken.None);
foreach (var episode in episodes.Take(5))
{
    Console.WriteLine($"{episode.Id} | {episode.Title} | S{episode.Season}E{episode.Episode} | {episode.EpisodeTitle} | {episode.Language}");
}

internal sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
    }
}
