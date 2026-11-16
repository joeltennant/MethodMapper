using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

internal sealed class CallAnalyzer(Solution solution, HierarchyReport report, AnalyzerOptions options)
{
    public async Task WriteAsync(IMethodSymbol root, CancellationToken token)
    {
        // Keep traversal state local so another report starts with fresh numbering.
        var visited = new Dictionary<string, int>();
        var checkedDocuments = new HashSet<DocumentId>();
        var omittedExternal = new HashSet<string>();
        var collector = new CallCollector(solution, token);

        // An explicit stack supports deep call chains and recursive methods.
        var pending = new Stack<(IMethodSymbol Method, int Depth, string Note)>();
        pending.Push((root, 0, "root"));

        while (pending.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();

            var method = await ResolveSourceAsync(entry.Method, token);
            var project = ResolveProject(method);
            var key = GetMethodKey(method, project);

            if (options.ExcludeExternal && project == null && entry.Depth > 0)
            {
                omittedExternal.Add(key);
                continue;
            }

            if (visited.TryGetValue(key, out var previous))
            {
                await report.WriteMethodAsync(method, project, entry.Depth, previous, true, entry.Note);
                continue;
            }

            // A limited method is not remembered, so a shallower path to it can still expand it.
            if (options.MaxDepth is { } limit && entry.Depth >= limit && project != null)
            {
                await report.WriteDepthLimitedAsync(method, project, entry.Depth, entry.Note);
                continue;
            }

            var id = visited.Count + 1;
            visited.Add(key, id);
            await report.WriteMethodAsync(method, project, entry.Depth, id, false, entry.Note);

            if (visited.Count % 100 == 0)
                Console.WriteLine($"Analyzed {visited.Count} methods...");

            var calls = await ReadCallsAsync(method, collector, entry.Depth, checkedDocuments, token);

            // Reverse the push order so the report keeps source traversal order.
            for (var index = calls.Count - 1; index >= 0; index--)
                pending.Push((calls[index].Method, entry.Depth + 1, calls[index].Note));
        }

        await report.WriteTotalAsync(visited.Count, omittedExternal.Count);
    }

    private async Task<IMethodSymbol> ResolveSourceAsync(IMethodSymbol method, CancellationToken token)
    {
        method = (method.ReducedFrom ?? method).OriginalDefinition;
        method = await SymbolFinder.FindSourceDefinitionAsync(method, solution, token) as IMethodSymbol ?? method;
        return method.PartialImplementationPart ?? method;
    }

    // Implicit members such as default constructors have no syntax, but still belong to a project.
    private Project? ResolveProject(IMethodSymbol method)
    {
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault();
        var project = declaration == null ? null : solution.GetDocument(declaration.SyntaxTree)?.Project;
        return project ?? solution.GetProject(method.ContainingAssembly);
    }

    private static string GetMethodKey(IMethodSymbol method, Project? project)
    {
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault();
        return $"{project?.Id}|{method.ContainingAssembly?.Identity}|{method.ToDisplayString()}"
            + $"|{declaration?.SyntaxTree.FilePath}|{declaration?.Span.Start}";
    }

    private async Task<List<(IMethodSymbol Method, string Note)>> ReadCallsAsync(IMethodSymbol method,
        CallCollector collector, int depth, HashSet<DocumentId> checkedDocuments, CancellationToken token)
    {
        var calls = new List<(IMethodSymbol Method, string Note)>();
        var isInstanceConstructor = method.MethodKind == MethodKind.Constructor;
        var implicitConstructor = isInstanceConstructor && method.DeclaringSyntaxReferences.Length == 0
            && method.ContainingType.DeclaringSyntaxReferences.Length > 0;

        if (method.DeclaringSyntaxReferences.Length == 0 && !implicitConstructor)
        {
            await report.WriteDetailAsync(depth, "[No source body available]");
            return calls;
        }

        if (implicitConstructor)
            await report.WriteDetailAsync(depth, "[Implicit constructor: field and property initializers only]");

        var chainsToThis = false;
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = await reference.GetSyntaxAsync(token);
            await report.WriteSourceAsync(syntax, depth);

            if (syntax is ConstructorDeclarationSyntax { Initializer: { } initializer }
                && initializer.IsKind(SyntaxKind.ThisConstructorInitializer))
                chainsToThis = true;

            var model = await GetModelAsync(reference.SyntaxTree, depth, checkedDocuments, token);
            if (model == null)
            {
                await report.WriteDetailAsync(depth, "WARNING: Source could not be analyzed; semantic model unavailable.");
                continue;
            }

            // A primary constructor's declaration is the type itself; only its base call is a body.
            if (syntax is TypeDeclarationSyntax typeDeclaration)
            {
                foreach (var baseCall in typeDeclaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>() ?? [])
                    if (model.GetOperation(baseCall, token) is { } baseOperation)
                        await CollectAsync(baseOperation, collector, calls, depth);
                continue;
            }

            var operation = GetBodyOperation(model, syntax, token);
            if (operation == null)
            {
                if (!HasNoBodyByDesign(method, syntax))
                    await report.WriteDetailAsync(depth, "WARNING: No analyzable body found; calls from this method are not listed.");
                continue;
            }

            await CollectAsync(operation, collector, calls, depth);
        }

        // Field and property initializers run as part of each constructor that does not chain to another.
        if (isInstanceConstructor && !chainsToThis)
            await ReadInitializersAsync(method.ContainingType, collector, calls, depth, checkedDocuments, token);

        return calls;
    }

    private async Task ReadInitializersAsync(INamedTypeSymbol type, CallCollector collector,
        List<(IMethodSymbol Method, string Note)> calls, int depth, HashSet<DocumentId> checkedDocuments,
        CancellationToken token)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (await reference.GetSyntaxAsync(token) is not TypeDeclarationSyntax declaration)
                continue;

            var clauses = new List<EqualsValueClauseSyntax>();
            foreach (var member in declaration.Members)
            {
                if (member.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword) || modifier.IsKind(SyntaxKind.ConstKeyword)))
                    continue;

                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        clauses.AddRange(field.Declaration.Variables.Select(variable => variable.Initializer)
                            .OfType<EqualsValueClauseSyntax>());
                        break;
                    case PropertyDeclarationSyntax { Initializer: { } propertyInitializer }:
                        clauses.Add(propertyInitializer);
                        break;
                }
            }

            if (clauses.Count == 0)
                continue;

            var model = await GetModelAsync(reference.SyntaxTree, depth, checkedDocuments, token);
            if (model == null)
            {
                await report.WriteDetailAsync(depth, "WARNING: Initializers could not be analyzed; semantic model unavailable.");
                continue;
            }

            foreach (var clause in clauses)
                if (model.GetOperation(clause, token) is { } operation)
                    await CollectAsync(operation, collector, calls, depth);
        }
    }

    private async Task CollectAsync(IOperation operation, CallCollector collector,
        List<(IMethodSymbol Method, string Note)> calls, int depth)
    {
        var collected = await collector.CollectAsync(operation);
        calls.AddRange(collected.Calls);
        foreach (var warning in collected.Warnings)
            await report.WriteDetailAsync(depth, warning);
    }

    // Each file is checked for compilation errors the first time any of its code is analyzed.
    private async Task<SemanticModel?> GetModelAsync(SyntaxTree tree, int depth,
        HashSet<DocumentId> checkedDocuments, CancellationToken token)
    {
        var document = solution.GetDocument(tree);
        if (document == null)
            return null;

        var model = await document.GetSemanticModelAsync(token);
        if (model != null && checkedDocuments.Add(document.Id))
        {
            var errors = model.GetDiagnostics(cancellationToken: token)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
                await report.WriteDocumentErrorsAsync(document.FilePath ?? document.Name, errors, depth);
        }

        return model;
    }

    private static bool HasNoBodyByDesign(IMethodSymbol method, SyntaxNode syntax)
    {
        if (method.IsAbstract || method.IsExtern || method.ContainingType.TypeKind == TypeKind.Interface)
            return true;

        return syntax switch
        {
            AccessorDeclarationSyntax accessor => accessor.Body == null && accessor.ExpressionBody == null,
            MethodDeclarationSyntax declaration => declaration.Body == null && declaration.ExpressionBody == null,
            _ => false
        };
    }

    private static IOperation? GetBodyOperation(SemanticModel model, SyntaxNode syntax, CancellationToken token)
    {
        var operation = model.GetOperation(syntax, token);
        if (operation != null)
            return operation;

        // Expression-bodied properties and indexers may share their getter's syntax.
        var expression = syntax switch
        {
            BaseMethodDeclarationSyntax method => method.ExpressionBody?.Expression,
            AccessorDeclarationSyntax accessor => accessor.ExpressionBody?.Expression,
            PropertyDeclarationSyntax property => property.ExpressionBody?.Expression,
            IndexerDeclarationSyntax indexer => indexer.ExpressionBody?.Expression,
            _ => null
        };

        return expression == null ? null : model.GetOperation(expression, token);
    }
}
