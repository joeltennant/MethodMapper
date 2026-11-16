using Microsoft.CodeAnalysis;

internal sealed class HierarchyReport(TextWriter writer)
{
    public async Task WriteHeaderAsync(AnalyzerOptions options)
    {
        await writer.WriteLineAsync($"Call hierarchy: {options.SolutionPath}");
        await writer.WriteLineAsync($"Configuration: {options.Configuration}; framework: {options.Framework ?? "project default"}");
        if (options.MaxDepth != null)
            await writer.WriteLineAsync($"Depth limit: {options.MaxDepth}");
        if (options.ExcludeExternal)
            await writer.WriteLineAsync("External methods (no source in the solution) are omitted.");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("Static C# source analysis. Each method is expanded once; [see #N] refers to its first expansion.");
        await writer.WriteLineAsync("Possible dispatch targets are alternatives, not proof of execution. Lambda bodies and method-group callbacks are included conservatively.");
        await writer.WriteLineAsync("Constructors include their instance field/property initializers. foreach, using and await include their enumerator, dispose and awaiter calls.");
        await writer.WriteLineAsync("Not traced: bodies of DLLs without source in the solution, reflection, dynamic calls, runtime delegate bindings,");
        await writer.WriteLineAsync("static constructors and static initializers, and implicit ToString/string-conversion calls.");
        await writer.WriteLineAsync("Only the loaded build configuration is analyzed. Compilation errors can make the report incomplete.");
        await writer.WriteLineAsync();
    }

    public async Task WriteWorkspaceDiagnosticsAsync(IEnumerable<WorkspaceDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var message = $"WORKSPACE {diagnostic.Kind}: {diagnostic.Message}";
            Console.Error.WriteLine(message);
            await writer.WriteLineAsync(message);
        }
    }

    public Task WriteMethodAsync(IMethodSymbol method, Project? project, int depth, int id, bool repeated, string note)
    {
        var number = repeated ? $"[see #{id}]" : $"[#{id}]";
        return writer.WriteLineAsync($"{Indent(depth)}{number} {Describe(method, project)} [{note}]");
    }

    // A method at the depth limit is listed but not expanded, so it is not numbered or remembered.
    public Task WriteDepthLimitedAsync(IMethodSymbol method, Project? project, int depth, string note)
    {
        return writer.WriteLineAsync($"{Indent(depth)}[depth limit] {Describe(method, project)} [{note}]");
    }

    public Task WriteSourceAsync(SyntaxNode syntax, int depth)
    {
        var line = syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return WriteDetailAsync(depth, $"Source: {syntax.SyntaxTree.FilePath}:{line}");
    }

    public async Task WriteDocumentErrorsAsync(string path, IReadOnlyList<Diagnostic> errors, int depth)
    {
        await WriteDetailAsync(depth, $"WARNING: {path} has {errors.Count} compilation errors; analysis may be incomplete.");
        foreach (var error in errors.Take(10))
            await WriteDetailAsync(depth, error.ToString());
    }

    public Task WriteDetailAsync(int depth, string message)
    {
        return writer.WriteLineAsync($"{Indent(depth)}  {message}");
    }

    public async Task WriteTotalAsync(int count, int externalOmitted)
    {
        await writer.WriteLineAsync();
        if (externalOmitted > 0)
            await writer.WriteLineAsync($"External methods omitted: {externalOmitted}");
        await writer.WriteLineAsync($"Unique methods: {count}");
    }

    private static string Describe(IMethodSymbol method, Project? project)
    {
        var signature = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return $"Method: {signature} | Class: {method.ContainingType?.ToDisplayString()}"
            + $" | Project: {project?.Name ?? "external / source unavailable"} | Assembly: {method.ContainingAssembly?.Name}";
    }

    private static string Indent(int depth) => new(' ', depth * 2);
}
