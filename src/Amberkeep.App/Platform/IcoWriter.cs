using System.Buffers.Binary;

namespace Amberkeep.App.Platform;

/// <summary>Writes a Windows .ico file whose images are stored as PNG (supported since Windows Vista).</summary>
public static class IcoWriter
{
    private const int HeaderSize = 6;
    private const int EntrySize = 16;

    /// <param name="images">Square PNG images with their size in pixels (1–256), e.g. from <see cref="LogoArt.Render"/>.</param>
    public static void Write(Stream output, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("An icon needs between 1 and 65535 images.", nameof(images));
        }

        var header = new byte[HeaderSize + EntrySize * images.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)images.Count);

        var offset = header.Length;
        for (var i = 0; i < images.Count; i++)
        {
            var (size, png) = images[i];
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256);

            var entry = header.AsSpan(HeaderSize + EntrySize * i, EntrySize);
            entry[0] = (byte)(size == 256 ? 0 : size); // width; 0 means 256
            entry[1] = entry[0];                        // height
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);  // color planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], png.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            offset += png.Length;
        }

        output.Write(header);
        foreach (var (_, png) in images)
        {
            output.Write(png);
        }
    }
}
