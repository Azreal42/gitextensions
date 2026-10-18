using System.Buffers.Binary;

namespace GitExtensions.Linux.Desktop.Avatars;

/// <summary>Encoded immutable data only. Display bitmaps belong to individual presenters.</summary>
internal sealed class AvatarPng
{
    private readonly byte[] _bytes;
    internal int Width { get; }
    internal int Height { get; }
    internal long Epoch { get; }

    internal AvatarPng(byte[] png, long epoch = 0)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length is < 33 or > AvatarImageTransport.MaximumResponseBytes
            || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        { throw new ArgumentException("Expected a bounded PNG payload.", nameof(png)); }
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width is < 1 or > AvatarImageCodec.MaximumImageDimension || height is < 1 or > AvatarImageCodec.MaximumImageDimension)
        { throw new ArgumentException("PNG dimensions exceed avatar limits.", nameof(png)); }
        Width = (int)width; Height = (int)height; Epoch = epoch; _bytes = png.ToArray();
    }

    private AvatarPng(AvatarPng source, long epoch)
    { _bytes = source._bytes; Width = source.Width; Height = source.Height; Epoch = epoch; }

    internal Stream OpenRead() => new MemoryStream(_bytes, 0, _bytes.Length, writable: false, publiclyVisible: false);
    internal byte[] CopyBytes() => _bytes.ToArray();
    internal AvatarPng WithEpoch(long epoch) => Epoch == epoch ? this : new(this, epoch);
}
