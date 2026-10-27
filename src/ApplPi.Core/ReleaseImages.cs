using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplPi.Core;

/// <summary>
/// The published images: each ApplPi OS release on GitHub carries applpi-os-&lt;version&gt;.img.xz, its .bmap and
/// SHA256SUMS. A download is kept in %LOCALAPPDATA%\ApplPi\images and checked against SHA256SUMS, so a release is only
/// fetched once, and a damaged or altered download is never used.
/// </summary>
public static partial class ReleaseImages
{
    const string Repository = "EarthstormSoftware/applpi-os";

    public static string Cache { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApplPi", "images");

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex ReleaseVersion();

    /// <summary>Whether text names a release ("latest", or a version such as v0.4.0) rather than a file.</summary>
    public static bool IsRelease(string text) => text == "latest" || ReleaseVersion().IsMatch(text);

    /// <summary>The SHA-256 of each file in a SHA256SUMS (sha256sum's format: "&lt;hex&gt;  &lt;name&gt;").</summary>
    public static Dictionary<string, string> ParseSums(string sums)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                found[parts[1].TrimStart('*')] = parts[0].ToLowerInvariant();
            }
        }
        return found;
    }

    /// <summary>
    /// The release's image, downloaded (with its .bmap) and checked, or found already downloaded: its path.
    /// progress(file, bytes so far, bytes in all) reports the image's download.
    /// </summary>
    public static string Download(string release, Action<string, long, long?> progress)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("applpi", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var api = release == "latest"
            ? $"https://api.github.com/repos/{Repository}/releases/latest"
            : $"https://api.github.com/repos/{Repository}/releases/tags/{release}";
        using var answer = http.GetAsync(api).GetAwaiter().GetResult();
        if (answer.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(release == "latest"
                ? "There's no published ApplPi OS release yet."
                : $"There's no published ApplPi OS release {release}.");
        }
        answer.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(answer.Content.ReadAsStream());
        var tag = json.RootElement.GetProperty("tag_name").GetString()!;
        var assets = json.RootElement.GetProperty("assets").EnumerateArray()
            .ToDictionary(asset => asset.GetProperty("name").GetString()!,
                          asset => asset.GetProperty("browser_download_url").GetString()!);

        var imageName = $"applpi-os-{tag}.img.xz";
        var bmapName = $"applpi-os-{tag}.bmap";
        if (!assets.TryGetValue(imageName, out var imageUrl) || !assets.TryGetValue("SHA256SUMS", out var sumsUrl))
        {
            throw new InvalidOperationException($"Release {tag} has no {imageName} and SHA256SUMS.");
        }
        var sums = ParseSums(http.GetStringAsync(sumsUrl).GetAwaiter().GetResult());

        Directory.CreateDirectory(Cache);
        var image = Path.Combine(Cache, imageName);
        Fetch(http, imageUrl, image, Expected(sums, imageName), (bytes, total) => progress(imageName, bytes, total));
        if (assets.TryGetValue(bmapName, out var bmapUrl))
        {
            Fetch(http, bmapUrl, Path.Combine(Cache, bmapName), Expected(sums, bmapName), (_, _) => { });
        }
        return image;
    }

    static string Expected(Dictionary<string, string> sums, string name) =>
        sums.TryGetValue(name, out var hash) ? hash : throw new InvalidOperationException($"SHA256SUMS has no {name}.");

    /// <summary>Downloads url to path unless a file with the expected SHA-256 is there already; checks the download.</summary>
    static void Fetch(HttpClient http, string url, string path, string sha256, Action<long, long?> progress)
    {
        if (File.Exists(path) && Hash(path) == sha256)
        {
            return;
        }
        var partial = path + ".part";
        using (var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var source = response.Content.ReadAsStream();
            using var target = File.Create(partial);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                target.Write(buffer, 0, read);
                done += read;
                progress(done, total);
            }
        }
        if (Hash(partial) != sha256)
        {
            File.Delete(partial);
            throw new InvalidOperationException($"{Path.GetFileName(path)} didn't match its SHA-256: not used.");
        }
        File.Move(partial, path, overwrite: true);
    }

    static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
