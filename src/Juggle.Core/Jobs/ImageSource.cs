namespace Juggle.Core.Jobs;

/// <summary>
/// The image to write: a file, or a published release (v0.4.0, or latest), downloaded and checked. With none named,
/// the newest in deploy (a build of your own), or else the latest release.
/// </summary>
public sealed class ImageSource(IJobHost host, string deployDirectory = "deploy",
    Func<string, Action<string, long, long?>, string>? download = null)
{
    readonly Func<string, Action<string, long, long?>, string> download = download ?? ReleaseImages.Download;

    /// <summary>The image's full path, or null (having said why) if there isn't one.</summary>
    public string? Find(string? image)
    {
        image ??= Directory.Exists(deployDirectory)
            ? Directory.GetFiles(deployDirectory, "juggle-os-*.img.xz").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault()
            : null;
        image ??= "latest";
        if (ReleaseImages.IsRelease(image) && !File.Exists(image))
        {
            return Download(image);
        }
        if (!File.Exists(image))
        {
            host.Problem($"No such image: {image}");
            return null;
        }
        return Path.GetFullPath(image);
    }

    string? Download(string release)
    {
        host.Say($"Getting Juggle OS {release} from GitHub");
        try
        {
            var image = download(release, (name, bytes, total) => host.Progress(new JobProgress(JobStage.Download, bytes, total, name)));
            host.Progress(null);
            host.Say($"  {Path.GetFileName(image)}, checked against the release's SHA256SUMS");
            return image;
        }
        catch (HttpRequestException error)
        {
            host.Progress(null);
            host.Problem($"Couldn't download it: {error.Message}");
            return null;
        }
    }
}
