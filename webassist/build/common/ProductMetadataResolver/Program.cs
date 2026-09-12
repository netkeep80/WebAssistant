using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProductMetadataResolver;

internal static class Program
{
    private const string SchemaId = "webassistant-product-metadata/v1";
    private static readonly string[] MetadataFields =
    [
        "applicationName",
        "installerBaseName",
        "fileDescription",
        "companyName",
        "copyright"
    ];

    private static readonly HashSet<string> AllowedProperties =
        new(["schema", .. MetadataFields], StringComparer.Ordinal);

    private static readonly HashSet<string> WixDefineBoundFields =
        new(["applicationName", "fileDescription", "companyName"], StringComparer.Ordinal);

    private static readonly Regex InstallerBaseNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            Resolve(options.DefaultsPath, options.OverridePath, options.OutputPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Product metadata resolution failed: {exception.Message}");
            return 1;
        }
    }

    private static void Resolve(string defaultsPath, string overridePath, string outputPath)
    {
        defaultsPath = Path.GetFullPath(defaultsPath);
        overridePath = Path.GetFullPath(overridePath);
        outputPath = Path.GetFullPath(outputPath);

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        if (!File.Exists(defaultsPath))
        {
            throw new FileNotFoundException("Не найден canonical product metadata defaults file.", defaultsPath);
        }

        var defaults = ReadMetadata(defaultsPath, requireComplete: true);
        var effective = new Dictionary<string, string>(defaults.Values, StringComparer.Ordinal);
        var metadataMode = "defaults";
        var metadataInputPath = defaultsPath;

        if (File.Exists(overridePath))
        {
            var metadataOverride = ReadMetadata(overridePath, requireComplete: false);
            foreach (var pair in metadataOverride.Values)
            {
                effective[pair.Key] = pair.Value;
            }

            metadataMode = "override";
            metadataInputPath = overridePath;
        }

        ValidateEffective(effective);

        var metadataInputSha256 = Sha256Hex(File.ReadAllBytes(metadataInputPath));
        var canonicalEffective = CanonicalEffectiveJson(effective);
        var effectiveMetadataSha256 = Sha256Hex(canonicalEffective);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new[]
        {
            $"metadataMode={metadataMode}",
            $"applicationNameBase64={Encode(effective["applicationName"])}",
            $"installerBaseNameBase64={Encode(effective["installerBaseName"])}",
            $"fileDescriptionBase64={Encode(effective["fileDescription"])}",
            $"companyNameBase64={Encode(effective["companyName"])}",
            $"copyrightBase64={Encode(effective["copyright"])}",
            $"metadataInputSha256={metadataInputSha256}",
            $"effectiveMetadataSha256={effectiveMetadataSha256}"
        };

        File.WriteAllLines(outputPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static ParsedMetadata ReadMetadata(string path, bool requireComplete)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Product metadata root должен быть JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? schema = null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"Duplicate product metadata property: {property.Name}");
            }

            if (!AllowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException($"Unknown or forbidden product metadata property: {property.Name}");
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Product metadata property '{property.Name}' must be a string.");
            }

            var value = property.Value.GetString()
                ?? throw new InvalidDataException($"Product metadata property '{property.Name}' is null.");

            if (property.Name == "schema")
            {
                schema = value;
                continue;
            }

            ValidateMetadataString(property.Name, value);
            values[property.Name] = value;
        }

        if (!string.Equals(schema, SchemaId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported product metadata schema: {schema ?? "<missing>"}");
        }

        if (requireComplete)
        {
            foreach (var field in MetadataFields)
            {
                if (!values.ContainsKey(field))
                {
                    throw new InvalidDataException($"Canonical defaults are missing required field: {field}");
                }
            }
        }

        return new ParsedMetadata(values);
    }

    private static void ValidateEffective(IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in MetadataFields)
        {
            if (!values.TryGetValue(field, out var value))
            {
                throw new InvalidDataException($"Effective product metadata is missing field: {field}");
            }

            ValidateMetadataString(field, value);
        }

        var installerBaseName = values["installerBaseName"];
        if (installerBaseName is "." or ".." || !InstallerBaseNamePattern.IsMatch(installerBaseName))
        {
            throw new InvalidDataException(
                "installerBaseName must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ and must not be '.' or '..'.");
        }
    }

    private static void ValidateMetadataString(string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Product metadata field '{field}' must not be empty.");
        }

        if (value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Product metadata field '{field}' contains control characters.");
        }

        if (WixDefineBoundFields.Contains(field) && value.Contains(';', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Product metadata field '{field}' must not contain ';' because WiX DefineConstants uses semicolon as a delimiter.");
        }

        if (value.EnumerateRunes().Take(257).Count() > 256)
        {
            throw new InvalidDataException($"Product metadata field '{field}' exceeds 256 Unicode scalar values.");
        }
    }

    private static byte[] CanonicalEffectiveJson(IReadOnlyDictionary<string, string> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaId);
            foreach (var field in MetadataFields)
            {
                writer.WriteString(field, values[field]);
            }
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ResolverOptions ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Expected --defaults, --override and --output argument pairs.");
            }

            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Duplicate argument: {args[index]}");
            }
        }

        var allowed = new HashSet<string>(["--defaults", "--override", "--output"], StringComparer.Ordinal);
        if (values.Keys.Any(key => !allowed.Contains(key)) || values.Count != allowed.Count)
        {
            throw new ArgumentException("Expected exactly --defaults, --override and --output.");
        }

        return new ResolverOptions(values["--defaults"], values["--override"], values["--output"]);
    }

    private sealed record ParsedMetadata(Dictionary<string, string> Values);
    private sealed record ResolverOptions(string DefaultsPath, string OverridePath, string OutputPath);
}
