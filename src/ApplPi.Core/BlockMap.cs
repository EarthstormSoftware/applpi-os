using System.Globalization;
using System.Xml.Linq;

namespace ApplPi.Core;

/// <summary>
/// The blocks of an image that hold data, from the .bmap file the build makes beside it (bmaptool's format). The rest
/// is file systems' free space, which needn't be written. File system bookkeeping that happens to be all zeros is
/// still listed, which is why this is used rather than skipping every block of zeros.
/// </summary>
public sealed class BlockMap
{
    readonly List<(long Start, long End)> ranges;

    BlockMap(List<(long Start, long End)> ranges) => this.ranges = ranges;

    /// <summary>The .bmap beside an image (applpi-os-v0.1.0.img.xz: applpi-os-v0.1.0.bmap), or null if there isn't one.</summary>
    public static BlockMap? ForImage(string imagePath)
    {
        var name = Path.GetFileName(imagePath);
        var stem = name.EndsWith(".img.xz", StringComparison.OrdinalIgnoreCase) ? name[..^".img.xz".Length] : null;
        var path = stem is null ? null : Path.Combine(Path.GetDirectoryName(imagePath)!, stem + ".bmap");
        return path is not null && File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    public static BlockMap Parse(string xml)
    {
        var bmap = XDocument.Parse(xml).Root ?? throw new FormatException("Empty block map.");
        var blockSize = long.Parse(bmap.Element("BlockSize")!.Value.Trim(), CultureInfo.InvariantCulture);
        var ranges = bmap.Element("BlockMap")!.Elements("Range")
            .Select(range => range.Value.Trim().Split('-'))
            .Select(bounds => (
                Start: long.Parse(bounds[0], CultureInfo.InvariantCulture) * blockSize,
                End: (long.Parse(bounds[^1], CultureInfo.InvariantCulture) + 1) * blockSize))
            .OrderBy(range => range.Start)
            .ToList();
        return new BlockMap(ranges);
    }

    /// <summary>
    /// The parts of the image between offset and offset + length that hold data, as (start, length) relative to
    /// offset. Block-aligned, since the ranges are whole blocks.
    /// </summary>
    public IEnumerable<(int Start, int Length)> Mapped(long offset, int length)
    {
        var end = offset + length;
        foreach (var range in ranges)
        {
            if (range.End <= offset)
            {
                continue;
            }
            if (range.Start >= end)
            {
                yield break;
            }
            var start = Math.Max(range.Start, offset);
            yield return ((int)(start - offset), (int)(Math.Min(range.End, end) - start));
        }
    }
}
