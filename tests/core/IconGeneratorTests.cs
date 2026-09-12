using System.Buffers.Binary;
using System.Security.Cryptography;
using IconGeneratorApi = WebAssistant.IconGenerator.IconGenerator;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class IconGeneratorTests
{
    [Fact]
    public void Generator_ProducesDeterministicIcoAnd64PxLogo()
    {
        var svgPath = ToFullPath("webassist/build/windows/installer/branding/webassistant-icon.svg");
        Assert.True(File.Exists(svgPath), "Canonical WebAssistant SVG is missing.");

        var tempRoot = CreateTempDirectory();
        try
        {
            var first = Path.Combine(tempRoot, "first");
            var second = Path.Combine(tempRoot, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);

            var firstIco = Path.Combine(first, "webassistant-icon.ico");
            var firstLogo = Path.Combine(first, "webassistant-logo.png");
            var secondIco = Path.Combine(second, "webassistant-icon.ico");
            var secondLogo = Path.Combine(second, "webassistant-logo.png");

            IconGeneratorApi.Generate(svgPath, firstIco, firstLogo);
            IconGeneratorApi.Generate(svgPath, secondIco, secondLogo);

            Assert.Equal(Sha256(firstIco), Sha256(secondIco));
            Assert.Equal(Sha256(firstLogo), Sha256(secondLogo));
            Assert.Equal((64, 64), ReadPngDimensions(firstLogo));
            Assert.Equal(new[] { 16, 32, 48, 256 }, ReadIcoDimensions(firstIco));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Generator_FailsClosedForMissingOrInvalidSvg()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var missingSvg = Path.Combine(tempRoot, "missing.svg");
            var invalidSvg = Path.Combine(tempRoot, "invalid.svg");
            File.WriteAllText(invalidSvg, "<svg><");

            foreach (var svgPath in new[] { missingSvg, invalidSvg })
            {
                var suffix = Path.GetFileNameWithoutExtension(svgPath);
                var icoPath = Path.Combine(tempRoot, $"{suffix}.ico");
                var logoPath = Path.Combine(tempRoot, $"{suffix}.png");

                Assert.ThrowsAny<Exception>(() => IconGeneratorApi.Generate(svgPath, icoPath, logoPath));
                Assert.False(File.Exists(icoPath));
                Assert.False(File.Exists(logoPath));
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static int[] ReadIcoDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        Assert.Equal((ushort)4, count);

        var dimensions = new int[count];
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadUInt32();
            reader.ReadUInt32();

            var normalizedWidth = width == 0 ? 256 : width;
            var normalizedHeight = height == 0 ? 256 : height;
            Assert.Equal(normalizedWidth, normalizedHeight);
            dimensions[index] = normalizedWidth;
        }

        return dimensions;
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 24, "PNG is too short.");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        return (
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "webassistant-icon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ToFullPath(string relativePath) =>
        Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "webassist")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")) &&
                File.Exists(Path.Combine(directory.FullName, "repo-policy.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Не найден корень репозитория WebAssistant.");
    }
}
