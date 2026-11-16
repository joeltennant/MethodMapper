using Microsoft.CodeAnalysis;

internal static class RootMethodFinder
{
    public static async Task<IMethodSymbol> FindAsync(Solution solution, AnalyzerOptions options, CancellationToken token)
    {
        var project = FindProject(solution, options);
        if (project.Language != LanguageNames.CSharp)
            throw new ArgumentException("The starting project must be a C# project.");

        var compilation = await project.GetCompilationAsync(token)
            ?? throw new InvalidOperationException("Cannot compile project for analysis.");

        // Look only in this project's assembly, not in its referenced DLLs.
        var type = compilation.Assembly.GetTypeByMetadataName(options.ClassName)
            ?? throw new ArgumentException($"Class '{options.ClassName}' not found in '{project.Name}'. Include its namespace; use + for nested classes and `1 for one generic parameter.");

        var methods = type.GetMembers().OfType<IMethodSymbol>().ToArray();
        var candidates = methods.Where(method => method.Name == options.MethodName).ToArray();

        if (candidates.Length == 0)
        {
            var available = string.Join(Environment.NewLine, methods
                .Where(method => !method.IsImplicitlyDeclared)
                .Select(method => method.ToDisplayString()));
            throw new ArgumentException($"Method '{options.MethodName}' not found on '{options.ClassName}'. Methods declared there:\n{available}");
        }

        var matches = candidates.Where(method => options.Signature == null
            || method.ToDisplayString().Contains(options.Signature, StringComparison.Ordinal)).ToArray();

        if (matches.Length != 1)
        {
            var signatures = string.Join(Environment.NewLine, (matches.Length > 1 ? matches : candidates)
                .Select(method => method.ToDisplayString()));
            throw new ArgumentException($"Expected one matching method, found {matches.Length}. Use --signature to select an overload:\n{signatures}");
        }

        return matches[0];
    }

    private static Project FindProject(Solution solution, AnalyzerOptions options)
    {
        var name = options.ProjectName;

        // A project that targets several frameworks is loaded once per framework and named "Name(tfm)".
        var projects = solution.Projects.Where(project => project.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Length == 0)
        {
            projects = solution.Projects
                .Where(project => project.Name.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (projects.Length > 1)
            {
                Console.WriteLine($"'{name}' targets several frameworks; using {projects[0].Name}. Use --framework to choose another.");
                return projects[0];
            }
        }

        if (projects.Length != 1)
        {
            var names = string.Join(Environment.NewLine, solution.Projects.Select(project => project.Name));
            throw new ArgumentException($"Expected one project named '{name}', found {projects.Length}. Loaded projects:\n{names}");
        }

        return projects[0];
    }
}
