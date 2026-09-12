namespace WebAssistant.IconGenerator;

internal static class Program
{
    private static readonly string[] RequiredOptions = ["--input", "--ico", "--logo"];

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            IconGenerator.Generate(options["--input"], options["--ico"], options["--logo"]);
            return 0;
        }
        catch (Exception exception)
        {
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            Console.Error.WriteLine($"WebAssistant icon generator: {message}");
            return 1;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length != RequiredOptions.Length * 2)
        {
            throw new ArgumentException("Expected --input <svg> --ico <ico-output> --logo <png-output>.");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            var value = args[index + 1];
            if (!RequiredOptions.Contains(option, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown option: {option}");
            }

            if (!options.TryAdd(option, value))
            {
                throw new ArgumentException($"Duplicate option: {option}");
            }

            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for option: {option}");
            }
        }

        foreach (var requiredOption in RequiredOptions)
        {
            if (!options.ContainsKey(requiredOption))
            {
                throw new ArgumentException($"Missing required option: {requiredOption}");
            }
        }

        return options;
    }
}
