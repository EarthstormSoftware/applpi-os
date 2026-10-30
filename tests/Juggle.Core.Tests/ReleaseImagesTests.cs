using Juggle.Core;

namespace Juggle.Core.Tests;

public class ReleaseImagesTests
{
    [Theory]
    [InlineData("latest", true)]
    [InlineData("v0.4.0", true)]
    [InlineData("v10.20.30", true)]
    [InlineData("0.4.0", false)]
    [InlineData("v0.4", false)]
    [InlineData("deploy\\juggle-os-v0.4.0.img.xz", false)]
    public void TellsAReleaseFromAFile(string text, bool release) => Assert.Equal(release, ReleaseImages.IsRelease(text));

    [Fact]
    public void ReadsSha256Sums()
    {
        var hash = new string('a', 64);
        var other = new string('B', 64);
        var sums = ReleaseImages.ParseSums(
            $"{hash}  juggle-os-v0.4.0.img.xz\n{other} *juggle-os-v0.4.0.bmap\r\nnot a line\n\n{hash[..10]}  short.txt\n");

        Assert.Equal(2, sums.Count);
        Assert.Equal(hash, sums["juggle-os-v0.4.0.img.xz"]);
        Assert.Equal(other.ToLowerInvariant(), sums["juggle-os-v0.4.0.bmap"]);
    }
}
