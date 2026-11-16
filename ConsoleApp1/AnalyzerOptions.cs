internal sealed class AnalyzerOptions
{
    private static readonly string[] ValueOptions =
        ["solution", "project", "class", "method", "signature", "out", "configuration", "framework", "max-depth"];
    private static readonly string[] FlagOptions = ["exclude-external", "verbose"];

    public required string SolutionPath { get; init; }
    public required string ProjectName { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public string? Signature { get; init; }
    public required string OutputPath { get; init; }
    public string Configuration { get; init; } = "Debug";
    public string? Framework { get; init; }
    public int? MaxDepth { get; init; }
    public bool ExcludeExternal { get; init; }
    public bool Verbose { get; init; }

    public static bool IsHelpRequest(string[] args) =>
        args.Any(arg => arg is "--help" or "-h" or "/?");

    public static AnalyzerOptions Parse(string[] args)
    {
        var values = ReadArguments(args, out var flags);
        var interactive = args.Length == 0;

        var options = new AnalyzerOptions
        {
            SolutionPath = Path.GetFullPath(ReadRequired(values, "solution", "Solution path", interactive)),
            ProjectName = ReadRequired(values, "project", "Project containing the method", interactive),
            ClassName = ReadRequired(values, "class", "Class including namespace", interactive),
            MethodName = ReadRequired(values, "method", "Method name", interactive),
            Signature = values.GetValueOrDefault("signature"),
            OutputPath = Path.GetFullPath(values.GetValueOrDefault("out", "call-hierarchy.txt")),
            Configuration = values.GetValueOrDefault("configuration", "Debug"),
            Framework = values.GetValueOrDefault("framework"),
            MaxDepth = ReadMaxDepth(values),
            ExcludeExternal = flags.Contains("exclude-external"),
            Verbose = flags.Contains("verbose")
        };

        options.ValidatePaths();
        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("MethodMapper --solution <path.sln|path.slnx> --project <name> --class <Namespace.Class> --method <name>");
        Console.WriteLine("  [--signature <text>] [--out <path.txt>] [--configuration Debug] [--framework <TFM>]");
        Console.WriteLine("  [--max-depth <n>] [--exclude-external] [--verbose]");
        Console.WriteLine();
        Console.WriteLine("  --signature         Fragment of the displayed signature, to pick one overload.");
        Console.WriteLine("  --max-depth         Do not expand methods deeper than n levels below the root.");
        Console.WriteLine("  --exclude-external  Omit methods whose source is not in the solution (framework, NuGet, DLLs).");
        Console.WriteLine("  --verbose           Show full exception details on failure.");
        Console.WriteLine("With no arguments, prompts for solution, project, class, and method.");
    }

    private static Dictionary<string, string> ReadArguments(string[] args, out HashSet<string> flags)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            var name = argument.StartsWith("--") ? argument[2..] : null;

            if (name != null && FlagOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                flags.Add(name);
                continue;
            }

            if (name == null || !ValueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown argument: {argument}. Use --help.");

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")
                || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"A value is required for {argument}.");

            if (!values.TryAdd(name, args[++index]))
                throw new ArgumentException($"Argument {argument} was supplied more than once.");
        }

        return values;
    }

    private static int? ReadMaxDepth(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("max-depth", out var text))
            return null;

        if (!int.TryParse(text, out var depth) || depth < 0)
            throw new ArgumentException("--max-depth must be a whole number of 0 or more.");

        return depth;
    }

    private static string ReadRequired(Dictionary<string, string> values, string name, string prompt, bool interactive)
    {
        if (values.TryGetValue(name, out var value))
            return value;

        if (!interactive)
            throw new ArgumentException($"Missing --{name}.");

        Console.Write(prompt + ": ");
        var answer = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(answer))
            throw new ArgumentException($"{name} is required.");

        return answer;
    }

    private void ValidatePaths()
    {
        if (!File.Exists(SolutionPath))
            throw new FileNotFoundException($"Solution not found: {SolutionPath}");

        var extension = Path.GetExtension(SolutionPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Supply a .sln or .slnx solution file.");

        if (!Path.GetExtension(OutputPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be a .txt file.");
    }
}
