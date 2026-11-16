using Microsoft.CodeAnalysis;

// Writes the same traversal as HierarchyReport as a Mermaid flowchart inside a Markdown file.
internal sealed class MermaidReport(TextWriter writer)
{
    private int limitedCount;

    public async Task WriteStartAsync(AnalyzerOptions options)
    {
        await writer.WriteLineAsync($"# Call hierarchy: {options.ClassName}.{options.MethodName}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("Node numbers match the `[#N]` numbers in the text report. Solid arrows are first expansions,");
        await writer.WriteLineAsync("dashed arrows point to a method already expanded, and dotted nodes stopped at the depth limit.");
        await writer.WriteLineAsync("Mermaid renders at most 500 edges and 50,000 characters by default; for large solutions use");
        await writer.WriteLineAsync("`--max-depth` and `--exclude-external`, or raise `maxEdges` and `maxTextSize`.");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("```mermaid");
        await writer.WriteLineAsync("flowchart TD");
        await writer.WriteLineAsync("    classDef limit stroke-dasharray: 3 3;");
    }

    // A parent id of 0 marks the root, which has no incoming edge.
    public async Task WriteNodeAsync(IMethodSymbol method, int id, int parentId, string note)
    {
        await writer.WriteLineAsync($"    {NodeId(id)}[\"#{id} {Label(method)}\"]");
        if (parentId > 0)
            await writer.WriteLineAsync($"    {NodeId(parentId)} -->{EdgeLabel(note)} {NodeId(id)}");
    }

    public Task WriteRepeatEdgeAsync(int parentId, int id, string note)
    {
        return writer.WriteLineAsync($"    {NodeId(parentId)} -.->{EdgeLabel(note)} {NodeId(id)}");
    }

    public async Task WriteDepthLimitedAsync(IMethodSymbol method, int parentId, string note)
    {
        var id = $"L{++limitedCount}";
        await writer.WriteLineAsync($"    {id}[\"{Label(method)}\"]:::limit");
        await writer.WriteLineAsync($"    {NodeId(parentId)} -.->{EdgeLabel(note)} {id}");
    }

    public async Task WriteEndAsync()
    {
        await writer.WriteLineAsync("```");
    }

    private static string NodeId(int id) => $"n{id}";

    private static string Label(IMethodSymbol method)
    {
        return Escape(method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    // Notes carry a source location; the diagram keeps only the kind of call.
    private static string EdgeLabel(string note)
    {
        var kind = note.StartsWith("possible dispatch", StringComparison.Ordinal)
            ? "possible dispatch"
            : note.Split(" at ")[0];
        return kind is "root" or "" ? "" : $"|{Escape(kind)}|";
    }

    // Mermaid entity codes keep quotes and angle brackets from breaking the syntax.
    private static string Escape(string text) => text
        .Replace("\"", "#quot;")
        .Replace("<", "#lt;")
        .Replace(">", "#gt;")
        .Replace("|", "#124;")
        .ReplaceLineEndings(" ");
}
