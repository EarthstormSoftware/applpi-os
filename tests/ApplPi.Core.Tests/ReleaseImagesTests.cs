using ApplPi.Core;

namespace ApplPi.Core.Tests;

public class ReleaseImagesTests
{
    [Theory]
    [InlineData("latest", true)]
    [InlineData("v0.4.0", true)]
    [InlineData("v10.20.30", true)]
    [InlineData("0.4.0", false)]
    [InlineData("v0.4", false)]
    [InlineData("deploy\\applpi-os-v0.4.0.img.xz", false)]
    public void TellsAReleaseFromAFile(string text, bool release) => Assert.Equal(release, ReleaseImages.IsRelease(text));

    [Fact]
    public void ReadsSha256Sums()
    {
        var hash = new string('a', 64);
        var other = new string('B', 64);
        var sums = ReleaseImages.ParseSums(
            $"{hash}  applpi-os-v0.4.0.img.xz\n{other} *applpi-os-v0.4.0.bmap\r\nnot a line\n\n{hash[..10]}  short.txt\n");

        Assert.Equal(2, sums.Count);
        Assert.Equal(hash, sums["applpi-os-v0.4.0.img.xz"]);
        Assert.Equal(other.ToLowerInvariant(), sums["applpi-os-v0.4.0.bmap"]);
    }
}
