using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

var verbose = args.Contains("--verbose");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Let the run stop cleanly so the temporary report is removed.
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

string? temporaryPath = null;
string? diagramTemporaryPath = null;
try
{
    if (AnalyzerOptions.IsHelpRequest(args))
    {
        AnalyzerOptions.PrintHelp();
        return 0;
    }

    var options = AnalyzerOptions.Parse(args);
    var token = cancellation.Token;

    // Register the installed build tools before creating the Roslyn workspace.
    MSBuildLocator.RegisterDefaults();
    var properties = new Dictionary<string, string>
    {
        ["Configuration"] = options.Configuration
    };
    if (options.Framework != null)
        properties["TargetFramework"] = options.Framework;

    using var workspace = MSBuildWorkspace.Create(properties);

    Console.WriteLine("Loading solution; a large solution may take several minutes...");
    var solution = await workspace.OpenSolutionAsync(options.SolutionPath, cancellationToken: token);
    var root = await RootMethodFinder.FindAsync(solution, options, token);

    // Write beside the destination, then move into place, so a failed or cancelled run
    // never replaces an earlier report with a truncated one.
    Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
    temporaryPath = options.OutputPath + ".tmp";
    var diagramPath = Path.ChangeExtension(options.OutputPath, ".md");
    diagramTemporaryPath = diagramPath + ".tmp";
    using (var writer = new StreamWriter(temporaryPath, false))
    using (var diagramWriter = new StreamWriter(diagramTemporaryPath, false))
    {
        var report = new HierarchyReport(writer);
        var diagram = new MermaidReport(diagramWriter);
        await report.WriteHeaderAsync(options);
        await diagram.WriteStartAsync(options);
        await report.WriteWorkspaceDiagnosticsAsync(workspace.Diagnostics);

        var analyzer = new CallAnalyzer(solution, report, diagram, options);
        await analyzer.WriteAsync(root, token);
        await diagram.WriteEndAsync();
    }

    File.Move(temporaryPath, options.OutputPath, overwrite: true);
    temporaryPath = null;
    File.Move(diagramTemporaryPath, diagramPath, overwrite: true);
    diagramTemporaryPath = null;
    Console.WriteLine($"Report written to {options.OutputPath}");
    Console.WriteLine($"Mermaid diagram written to {diagramPath}");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. No report was written.");
    return 130;
}
catch (Exception exception)
{
    // Expected input problems get a short message; anything else gets the full details.
    var expected = exception is ArgumentException or FileNotFoundException;
    Console.Error.WriteLine(verbose || !expected ? exception.ToString() : exception.Message);
    return 1;
}
finally
{
    if (temporaryPath != null && File.Exists(temporaryPath))
        File.Delete(temporaryPath);
    if (diagramTemporaryPath != null && File.Exists(diagramTemporaryPath))
        File.Delete(diagramTemporaryPath);
}
