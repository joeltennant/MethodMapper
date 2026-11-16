using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

internal sealed class CallCollector(Solution solution, CancellationToken token)
{
    private readonly Dictionary<IMethodSymbol, IMethodSymbol[]> dispatchCache = new(SymbolEqualityComparer.Default);

    public async Task<(List<(IMethodSymbol Method, string Note)> Calls, List<string> Warnings)> CollectAsync(IOperation body)
    {
        var calls = new List<(IMethodSymbol Method, string Note)>();
        var warnings = new List<string>();
        var pending = new Stack<IOperation>();
        pending.Push(body);

        while (pending.TryPop(out var operation))
        {
            // nameof produces a constant; its operand does not execute.
            if (operation is INameOfOperation)
                continue;

            // A local function's body belongs to its call, not its declaration.
            if (operation is ILocalFunctionOperation && operation != body)
                continue;

            await CollectOperationAsync(operation, calls, warnings);
            foreach (var child in operation.ChildOperations.Reverse())
                pending.Push(child);
        }

        return (calls, warnings);
    }

    private async Task CollectOperationAsync(IOperation operation,
        List<(IMethodSymbol Method, string Note)> calls, List<string> warnings)
    {
        var location = GetLocation(operation);
        switch (operation)
        {
            case IInvocationOperation invocation:
                calls.Add((invocation.TargetMethod, $"call at {location}"));
                if (invocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
                    warnings.Add($"UNRESOLVED delegate target at {location}");
                if (invocation.IsVirtual)
                    await AddDispatchAsync(invocation.TargetMethod, calls, location);
                break;

            case IObjectCreationOperation { Constructor: { } constructor }:
                calls.Add((constructor, $"constructor at {location}"));
                break;

            case IPropertyReferenceOperation property:
                await CollectPropertyAsync(property, calls, location);
                break;

            case IMethodReferenceOperation reference:
                calls.Add((reference.Method, $"possible method-group callback at {location}"));
                if (reference.IsVirtual)
                    await AddDispatchAsync(reference.Method, calls, location);
                break;

            case IBinaryOperation { OperatorMethod: { } binary }:
                calls.Add((binary, $"operator at {location}"));
                break;

            case IUnaryOperation { OperatorMethod: { } unary }:
                calls.Add((unary, $"operator at {location}"));
                break;

            case IConversionOperation { OperatorMethod: { } conversion }:
                calls.Add((conversion, $"conversion at {location}"));
                break;

            case ICompoundAssignmentOperation { OperatorMethod: { } compound }:
                calls.Add((compound, $"operator at {location}"));
                break;

            case IIncrementOrDecrementOperation { OperatorMethod: { } increment }:
                calls.Add((increment, $"operator at {location}"));
                break;

            case IEventAssignmentOperation { EventReference: IEventReferenceOperation eventReference } assignment:
                var accessor = assignment.Adds ? eventReference.Event.AddMethod : eventReference.Event.RemoveMethod;
                if (accessor != null)
                {
                    calls.Add((accessor, $"event accessor at {location}"));
                    if (CanDispatch(accessor, eventReference.Syntax))
                        await AddDispatchAsync(accessor, calls, location);
                }
                break;

            // The compiler calls these implicitly, but a user-defined type's versions have real bodies.
            case IForEachLoopOperation { Syntax: CommonForEachStatementSyntax loopSyntax } loop
                when loop.SemanticModel is { } loopModel:
                var loopInfo = loopModel.GetForEachStatementInfo(loopSyntax);
                await AddImplicitAsync(loopInfo.GetEnumeratorMethod, "foreach enumerator", calls, location, loopSyntax);
                await AddImplicitAsync(loopInfo.MoveNextMethod, "foreach MoveNext", calls, location, loopSyntax, dispatch: false);
                await AddImplicitAsync(loopInfo.CurrentProperty?.GetMethod, "foreach Current", calls, location, loopSyntax, dispatch: false);
                await AddImplicitAsync(loopInfo.DisposeMethod, "foreach Dispose", calls, location, loopSyntax, dispatch: false);
                break;

            case IUsingOperation usingBlock when usingBlock.SemanticModel is { } usingModel:
                foreach (var type in GetResourceTypes(usingBlock.Resources))
                    await AddDisposeAsync(type, usingBlock.IsAsynchronous, usingModel.Compilation, calls, location, usingBlock.Syntax);
                break;

            case IUsingDeclarationOperation usingDeclaration when usingDeclaration.SemanticModel is { } declarationModel:
                foreach (var type in GetResourceTypes(usingDeclaration.DeclarationGroup))
                    await AddDisposeAsync(type, usingDeclaration.IsAsynchronous, declarationModel.Compilation, calls, location, usingDeclaration.Syntax);
                break;

            case IAwaitOperation { Syntax: AwaitExpressionSyntax awaitSyntax } awaitOperation
                when awaitOperation.SemanticModel is { } awaitModel:
                var awaitInfo = awaitModel.GetAwaitExpressionInfo(awaitSyntax);
                await AddImplicitAsync(awaitInfo.GetAwaiterMethod, "await GetAwaiter", calls, location, awaitSyntax);
                await AddImplicitAsync(awaitInfo.IsCompletedProperty?.GetMethod, "await IsCompleted", calls, location, awaitSyntax, dispatch: false);
                await AddImplicitAsync(awaitInfo.GetResultMethod, "await GetResult", calls, location, awaitSyntax, dispatch: false);
                break;

            case IDynamicInvocationOperation:
            case IInvalidOperation:
                warnings.Add($"UNRESOLVED {operation.Kind} at {location}: {operation.Syntax.ToString().ReplaceLineEndings(" ")}");
                break;
        }
    }

    private async Task CollectPropertyAsync(IPropertyReferenceOperation property,
        List<(IMethodSymbol Method, string Note)> calls, string location)
    {
        var parent = property.Parent;
        var writeOnly = parent is ISimpleAssignmentOperation assignment && assignment.Target == property;
        var readAndWrite = parent is ICompoundAssignmentOperation compound && compound.Target == property
            || parent is IIncrementOrDecrementOperation increment && increment.Target == property
            || parent is ICoalesceAssignmentOperation coalesce && coalesce.Target == property;

        // Assignment through a ref-returning property still calls its getter.
        var returnsReference = property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly;
        if ((!writeOnly || returnsReference) && property.Property.GetMethod is { } getter)
        {
            calls.Add((getter, $"getter at {location}"));
            if (CanDispatch(getter, property.Syntax))
                await AddDispatchAsync(getter, calls, location);
        }

        if ((writeOnly || readAndWrite) && property.Property.SetMethod is { } setter)
        {
            calls.Add((setter, $"setter at {location}"));
            if (CanDispatch(setter, property.Syntax))
                await AddDispatchAsync(setter, calls, location);
        }
    }

    private static bool CanDispatch(IMethodSymbol method, SyntaxNode syntax)
    {
        // base.Property and base.Event explicitly bypass virtual dispatch.
        var baseAccess = syntax is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }
            or ElementAccessExpressionSyntax { Expression: BaseExpressionSyntax };

        return !baseAccess && !method.IsStatic && !method.IsSealed
            && (method.IsVirtual || method.IsAbstract || method.IsOverride);
    }

    private static IEnumerable<ITypeSymbol?> GetResourceTypes(IOperation resources)
    {
        if (resources is IVariableDeclarationGroupOperation group)
            return group.Declarations.SelectMany(declaration => declaration.Declarators)
                .Select(declarator => (ITypeSymbol?)declarator.Symbol.Type);

        return new[] { resources.Type };
    }

    // Roslyn does not expose the Dispose target of a using statement, so find it from the resource type.
    private async Task AddDisposeAsync(ITypeSymbol? type, bool asynchronous, Compilation compilation,
        List<(IMethodSymbol Method, string Note)> calls, string location, SyntaxNode syntax)
    {
        if (type == null)
            return;

        var name = asynchronous ? "DisposeAsync" : "Dispose";
        var contract = compilation.GetTypeByMetadataName(asynchronous ? "System.IAsyncDisposable" : "System.IDisposable");
        var contractMethod = contract?.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault();

        IMethodSymbol? target = null;
        if (type.TypeKind != TypeKind.Interface && contractMethod != null)
            target = type.FindImplementationForInterfaceMember(contractMethod) as IMethodSymbol;

        // A ref struct may supply a Dispose method without implementing the interface.
        target ??= type.GetMembers(name).OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 0 && !method.IsStatic);
        target ??= contractMethod;

        await AddImplicitAsync(target, "using Dispose", calls, location, syntax);
    }

    private async Task AddImplicitAsync(IMethodSymbol? method, string kind,
        List<(IMethodSymbol Method, string Note)> calls, string location, SyntaxNode syntax, bool dispatch = true)
    {
        if (method == null)
            return;

        // Enumerator and awaiter members are usually reached through interfaces, where dispatching
        // would list every implementation in the solution; only the user-visible entry points dispatch.
        calls.Add((method, $"{kind} at {location}"));
        if (dispatch && CanDispatch(method, syntax))
            await AddDispatchAsync(method, calls, location);
    }

    private async Task AddDispatchAsync(IMethodSymbol method,
        List<(IMethodSymbol Method, string Note)> calls, string location)
    {
        method = method.OriginalDefinition;
        if (!dispatchCache.TryGetValue(method, out var targets))
        {
            var symbols = method.ContainingType.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindImplementationsAsync(method, solution, cancellationToken: token)
                : await SymbolFinder.FindOverridesAsync(method, solution, cancellationToken: token);

            targets = symbols.OfType<IMethodSymbol>().ToArray();
            dispatchCache.Add(method, targets);
        }

        foreach (var target in targets)
            calls.Add((target, $"possible dispatch from {method.ToDisplayString()} at {location}"));
    }

    private static string GetLocation(IOperation operation)
    {
        var line = operation.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return $"{Path.GetFileName(operation.Syntax.SyntaxTree.FilePath)}:{line}";
    }
}
