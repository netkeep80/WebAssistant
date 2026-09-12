using System.Buffers.Binary;
using SkiaSharp;
using Svg.Skia;

namespace WebAssistant.IconGenerator;

public static class IconGenerator
{
    private static readonly int[] IconSizes = [16, 32, 48, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Generate(string svgPath, string icoPath, string logoPath)
    {
        ValidatePath(svgPath, nameof(svgPath));
        ValidatePath(icoPath, nameof(icoPath));
        ValidatePath(logoPath, nameof(logoPath));

        var fullSvgPath = Path.GetFullPath(svgPath);
        var fullIcoPath = Path.GetFullPath(icoPath);
        var fullLogoPath = Path.GetFullPath(logoPath);

        if (!File.Exists(fullSvgPath))
        {
            throw new FileNotFoundException("Canonical SVG file was not found.", fullSvgPath);
        }

        if (string.Equals(fullIcoPath, fullLogoPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullSvgPath, fullIcoPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullSvgPath, fullLogoPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Input and output paths must be distinct.");
        }

        var icoTempPath = fullIcoPath + ".tmp";
        var logoTempPath = fullLogoPath + ".tmp";

        DeleteIfExists(icoTempPath);
        DeleteIfExists(logoTempPath);

        try
        {
            using var svg = new SKSvg();
            var picture = svg.Load(fullSvgPath);
            if (picture is null)
            {
                throw new InvalidDataException("SVG could not be rendered.");
            }

            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0 ||
                float.IsNaN(bounds.Width) || float.IsNaN(bounds.Height) ||
                float.IsInfinity(bounds.Width) || float.IsInfinity(bounds.Height))
            {
                throw new InvalidDataException("SVG has invalid render bounds.");
            }

            var iconFrames = IconSizes
                .Select(size => RenderPng(picture, bounds, size))
                .ToArray();
            var logo = RenderPng(picture, bounds, 64);
            var ico = BuildIco(iconFrames);

            File.WriteAllBytes(icoTempPath, ico);
            File.WriteAllBytes(logoTempPath, logo);

            ValidateIco(icoTempPath);
            ValidatePng(logoTempPath, 64);

            File.Move(icoTempPath, fullIcoPath, overwrite: true);
            File.Move(logoTempPath, fullLogoPath, overwrite: true);
        }
        catch
        {
            DeleteIfExists(icoTempPath);
            DeleteIfExists(logoTempPath);
            DeleteIfExists(fullIcoPath);
            DeleteIfExists(fullLogoPath);
            throw;
        }
    }

    private static byte[] RenderPng(SKPicture picture, SKRect bounds, int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ??
            throw new InvalidOperationException($"Unable to create {size}x{size} render surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var scale = Math.Min(size / bounds.Width, size / bounds.Height);
        var centerX = (bounds.Left + bounds.Right) / 2f;
        var centerY = (bounds.Top + bounds.Bottom) / 2f;

        canvas.Save();
        canvas.Translate(size / 2f, size / 2f);
        canvas.Scale(scale, scale);
        canvas.Translate(-centerX, -centerY);
        canvas.DrawPicture(picture);
        canvas.Restore();
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ??
            throw new InvalidOperationException($"Unable to encode {size}x{size} PNG.");
        var bytes = data.ToArray();
        ValidatePng(bytes, size);
        return bytes;
    }

    private static byte[] BuildIco(IReadOnlyList<byte[]> frames)
    {
        if (frames.Count != IconSizes.Length)
        {
            throw new ArgumentException("ICO requires exactly four frames.", nameof(frames));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        var offset = checked((uint)(6 + (frames.Count * 16)));
        for (var index = 0; index < frames.Count; index++)
        {
            var size = IconSizes[index];
            var payload = frames[index];
            ValidatePng(payload, size);

            writer.Write(size == 256 ? (byte)0 : checked((byte)size));
            writer.Write(size == 256 ? (byte)0 : checked((byte)size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(checked((uint)payload.Length));
            writer.Write(offset);
            offset = checked(offset + (uint)payload.Length);
        }

        foreach (var payload in frames)
        {
            writer.Write(payload);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void ValidateIco(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != IconSizes.Length)
        {
            throw new InvalidDataException("Generated ICO header is invalid.");
        }

        for (var index = 0; index < IconSizes.Length; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            if (reader.ReadUInt16() != 1 || reader.ReadUInt16() != 32)
            {
                throw new InvalidDataException("Generated ICO entry metadata is invalid.");
            }

            var payloadLength = reader.ReadUInt32();
            var payloadOffset = reader.ReadUInt32();
            var expected = IconSizes[index];
            if ((width == 0 ? 256 : width) != expected ||
                (height == 0 ? 256 : height) != expected ||
                payloadLength == 0 ||
                payloadOffset + payloadLength > stream.Length)
            {
                throw new InvalidDataException("Generated ICO entry is invalid.");
            }
        }
    }

    private static void ValidatePng(string path, int expectedSize) =>
        ValidatePng(File.ReadAllBytes(path), expectedSize);

    private static void ValidatePng(byte[] bytes, int expectedSize)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != expectedSize ||
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != expectedSize)
        {
            throw new InvalidDataException($"Generated PNG is not {expectedSize}x{expectedSize}.");
        }
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", parameterName);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
