using Juggle.Core;
using SharpCompress.Compressors.Xz;

namespace Juggle.Core.Tests;

/// <summary>
/// Writes a real image to a file as flash writes it to a card. Needs an image with its .bmap, so it runs only when
/// JUGGLE_TEST_IMAGE names one (such as deploy\juggle-os-v0.1.0.img.xz); it writes about 1.6 GB to the temp folder.
/// </summary>
public sealed class ImageWriterTests : IDisposable
{
    readonly string card = Path.Combine(Path.GetTempPath(), $"juggle-card-{Guid.NewGuid():N}.img");

    public void Dispose() => File.Delete(card);

    [Fact]
    public void WritesTheMappedBlocksOfTheImage()
    {
        var image = Environment.GetEnvironmentVariable("JUGGLE_TEST_IMAGE");
        if (string.IsNullOrEmpty(image))
        {
            return;
        }
        var map = BlockMap.ForImage(image) ?? throw new InvalidOperationException("The test image needs its .bmap.");

        var reports = new List<WriteProgress>();
        using (var file = ImageWriter.Open(CreateEmpty(card)))
        {
            ImageWriter.WriteTo(file, 8L << 30, image, new SyncProgress(reports.Add));
        }

        // Progress counted the mapped data, for writing and then checking
        Assert.Equal(map.MappedBytes, reports.Last(report => !report.Verifying).Bytes);
        Assert.Equal(map.MappedBytes, reports.Last().Bytes);
        Assert.True(reports.Last().Verifying);

        // Every mapped block on the "card" matches the image
        using var expected = new XZStream(File.OpenRead(image));
        using var actual = File.OpenRead(card);
        var imageChunk = new byte[4 << 20];
        var cardChunk = new byte[4 << 20];
        long offset = 0;
        int length;
        while ((length = ReadFully(expected, imageChunk)) > 0)
        {
            foreach (var (start, partLength) in map.Mapped(offset, length))
            {
                actual.Position = offset + start;
                Assert.Equal(partLength, ReadFully(actual, cardChunk.AsMemory(0, partLength)));
                Assert.True(cardChunk.AsSpan(0, partLength).SequenceEqual(imageChunk.AsSpan(start, partLength)),
                    $"Mismatch at {offset + start}");
            }
            offset += length;
        }
        Assert.Equal(map.ImageSize, offset);
    }

    static string CreateEmpty(string path)
    {
        File.WriteAllBytes(path, []);
        return path;
    }

    static int ReadFully(Stream stream, Memory<byte> buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer.Span[total..])) > 0)
        {
            total += read;
        }
        return total;
    }

    sealed class SyncProgress(Action<WriteProgress> report) : IProgress<WriteProgress>
    {
        public void Report(WriteProgress value) => report(value);
    }
}
