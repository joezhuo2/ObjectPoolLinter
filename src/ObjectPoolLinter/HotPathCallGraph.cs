using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    // The methods the hot flag reaches through calls: everything a hot method calls, and what those
    // call, up to object_pool_linter.max_call_depth calls away. Built once per compilation, walking
    // forward from every hot method, so its cost follows the code the hot methods reach rather than the
    // size of the project. Each wave of the walk outward from the hot methods is scanned in parallel, and
    // a call is bound only when some method in the project has the name it calls, so a hot method that
    // calls nothing but engine and library code costs one syntax walk.
    //
    // A call is followed when it executes in the calling method's own body, as GetExecutingMethod
    // decides for an allocation: a lambda passed elsewhere or a local function the body never calls is
    // not part of the frame. A call to a virtual, abstract or interface method is followed into every
    // override and implementation in source, since any of them may run. Only method bodies are walked;
    // constructors, property accessors and operators are not, and neither is code from referenced
    // assemblies. A call behind a guard the suppressor knows (`#if UNITY_EDITOR`, a first-frame check,
    // a static latch) is not followed, since it does not run every frame.
    //
    // Calls OPL002 already reports as allocations are not followed either, so one allocation is not
    // reported twice: an iterator, whose body runs as it is enumerated rather than when it is called,
    // and a LINQ-style extension on IEnumerable. Neither is a type whose name ends in `Pool`: a pool
    // allocates only when it runs empty, which is what it is for.
    internal sealed class HotPathCallGraph
    {
        private const string ChainSeparator = " -> ";
        private const string PoolSuffix = "Pool";

        private static readonly SymbolDisplayFormat ChainFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

        private readonly Dictionary<IMethodSymbol, Reach> _reached;

        private HotPathCallGraph(Dictionary<IMethodSymbol, Reach> reached)
        {
            _reached = reached;
        }

        // `Enemy.Update -> Enemy.Spawn -> PathFinder.Build` for a method the hot flag reached through
        // calls; null for one it did not reach, or for a hot method itself.
        internal string? GetCallChain(IMethodSymbol method)
        {
            if (!_reached.TryGetValue(Normalize(method), out var reach) || reach.Caller == null) return null;

            var chain = new List<string>();
            for (IMethodSymbol? current = Normalize(method); current != null; current = _reached[current].Caller)
                chain.Add(current.ToDisplayString(ChainFormat));

            chain.Reverse();
            return string.Join(ChainSeparator, chain);
        }

        internal static HotPathCallGraph Build(
            Compilation compilation,
            HotPathDetector detector,
            AnalyzerOptions analyzerOptions,
            CancellationToken cancellationToken)
        {
            return new Builder(compilation, detector, analyzerOptions, cancellationToken).Build();
        }

        // A partial method is called through its definition part, and a generic or extension method
        // through a constructed or reduced form; all of them map to the one declared symbol.
        private static IMethodSymbol Normalize(IMethodSymbol method)
        {
            method = method.ReducedFrom ?? method;
            method = method.OriginalDefinition;
            return method.PartialDefinitionPart ?? method;
        }

        // Budget is how many more calls the hot flag may still travel; Caller is null for a hot method.
        private readonly struct Reach
        {
            internal Reach(int budget, IMethodSymbol? caller)
            {
                Budget = budget;
                Caller = caller;
            }

            internal int Budget { get; }

            internal IMethodSymbol? Caller { get; }
        }

        private sealed class Builder
        {
            private readonly Compilation _compilation;
            private readonly HotPathDetector _detector;
            private readonly AnalyzerOptions _analyzerOptions;
            private readonly CancellationToken _cancellationToken;

            private readonly Dictionary<IMethodSymbol, Reach> _reached = new(SymbolEqualityComparer.Default);
            private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _semanticModels = new();
            private readonly ConcurrentDictionary<IMethodSymbol, List<IMethodSymbol>> _dispatchTargets = new(SymbolEqualityComparer.Default);
            private readonly Lazy<List<INamedTypeSymbol>> _sourceTypes;

            // The name of every method declared in source: a call to any other name cannot reach one.
            private readonly HashSet<string> _sourceMethodNames = new(StringComparer.Ordinal);

            internal Builder(
                Compilation compilation,
                HotPathDetector detector,
                AnalyzerOptions analyzerOptions,
                CancellationToken cancellationToken)
            {
                _compilation = compilation;
                _detector = detector;
                _analyzerOptions = analyzerOptions;
                _cancellationToken = cancellationToken;
                _sourceTypes = new Lazy<List<INamedTypeSymbol>>(CollectSourceTypes, LazyThreadSafetyMode.ExecutionAndPublication);
            }

            internal HotPathCallGraph Build()
            {
                var wave = new List<IMethodSymbol>();
                foreach (var (method, depth) in FindHotMethods())
                {
                    if (Visit(method, depth, caller: null)) wave.Add(Normalize(method));
                }

                while (wave.Count > 0)
                {
                    var callers = wave
                        .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                        .Select(method => (Method: method, _reached[method].Budget))
                        .Where(caller => caller.Budget > 0)
                        .ToList();

                    var callees = RunInParallel(callers.Count, i => GetCallees(callers[i].Method).ToList());

                    wave = new List<IMethodSymbol>();
                    for (var i = 0; i < callers.Count; i++)
                    {
                        foreach (var callee in callees[i])
                        {
                            if (Visit(callee, callers[i].Budget - 1, callers[i].Method)) wave.Add(Normalize(callee));
                        }
                    }
                }

                return new HotPathCallGraph(_reached);
            }

            // The roots, with the depth configured for the file each is declared in, and the name of every
            // method in source along the way. The name is checked first, so the semantic model is only
            // asked for in files that declare a candidate.
            private List<(IMethodSymbol Method, int Depth)> FindHotMethods()
            {
                var trees = _compilation.SyntaxTrees.ToList();
                var perTree = RunInParallel(trees.Count, i =>
                {
                    var tree = trees[i];
                    var options = _detector.GetOptions(tree, _analyzerOptions);
                    var names = new List<string>();
                    var hot = new List<(IMethodSymbol, int)>();

                    var root = tree.GetRoot(_cancellationToken);
                    foreach (var declaration in root.DescendantNodes(DescendIntoMembers).OfType<MethodDeclarationSyntax>())
                    {
                        var name = declaration.Identifier.ValueText;
                        names.Add(name);

                        if (options.MaxCallDepth <= 0 || !HotPathDetector.MayBeHotRoot(name, options)) continue;

                        if (GetSemanticModel(tree).GetDeclaredSymbol(declaration, _cancellationToken) is not { } method) continue;
                        if (_detector.IsHotRoot(method, options)) hot.Add((method, options.MaxCallDepth));
                    }

                    return (Names: names, Hot: hot);
                });

                var hotMethods = new List<(IMethodSymbol, int)>();
                foreach (var (names, hot) in perTree)
                {
                    _sourceMethodNames.UnionWith(names);
                    hotMethods.AddRange(hot);
                }

                return hotMethods;
            }

            // Runs `work` for 0..count-1 on the thread pool. Analysis threads waiting for the graph would
            // otherwise sit idle while one thread binds every hot method.
            private T[] RunInParallel<T>(int count, Func<int, T> work)
            {
                var results = new T[count];
                try
                {
                    Parallel.For(
                        0,
                        count,
                        new ParallelOptions { CancellationToken = _cancellationToken },
                        i => results[i] = work(i));
                }
                catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
                {
                    throw new OperationCanceledException(_cancellationToken);
                }

                return results;
            }

            // Namespaces and types hold the method declarations; member bodies do not.
            private static bool DescendIntoMembers(SyntaxNode node) =>
                node is not (BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax);

            // Whether the method was reached with more budget than before, and so needs walking again.
            private bool Visit(IMethodSymbol method, int budget, IMethodSymbol? caller)
            {
                method = Normalize(method);

                if (_reached.TryGetValue(method, out var existing))
                {
                    // A method already reached with at least this much budget left has nothing new to
                    // give, which is also what stops recursion.
                    if (existing.Budget >= budget) return false;

                    // A hot method keeps no caller, so its chain stays empty.
                    if (existing.Caller == null) caller = null;
                }

                _reached[method] = new Reach(budget, caller);
                return true;
            }

            private IEnumerable<IMethodSymbol> GetCallees(IMethodSymbol caller)
            {
                // A partial method's body is in its implementation part.
                var declared = caller.PartialImplementationPart ?? caller;

                foreach (var reference in declared.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(_cancellationToken) is not MethodDeclarationSyntax declaration) continue;

                    SyntaxNode? body = declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody;
                    if (body == null) continue;

                    var semanticModel = GetSemanticModel(declaration.SyntaxTree);
                    var options = _detector.GetOptions(declaration.SyntaxTree, _analyzerOptions);

                    foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (InvokedName(invocation.Expression) is not { } name || !_sourceMethodNames.Contains(name)) continue;

                        if (HotPathDetector.GetExecutingMethod(invocation, semanticModel) != declaration) continue;

                        if (semanticModel.GetSymbolInfo(invocation, _cancellationToken).Symbol is not IMethodSymbol target) continue;
                        if (target.MethodKind is not (MethodKind.Ordinary or MethodKind.ReducedExtension)) continue;
                        if (IsLinqStyleExtension(target)) continue;

                        if (ObjectPoolSuppressionAnalyzer.IsGuardedCall(invocation, semanticModel, options, _cancellationToken)) continue;

                        foreach (var callee in GetTargets(target, IsBaseCall(invocation)))
                        {
                            if (IsWalkable(callee)) yield return callee;
                        }
                    }
                }
            }

            // `Spawn()`, `Spawn<T>()`, `enemy.Spawn()`, `enemy?.Spawn()`; null for a delegate invocation.
            private static string? InvokedName(ExpressionSyntax expression) =>
                expression switch
                {
                    SimpleNameSyntax name => name.Identifier.ValueText,
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
                    _ => null,
                };

            private static bool IsLinqStyleExtension(IMethodSymbol method)
            {
                method = method.ReducedFrom ?? method;
                if (!method.IsExtensionMethod || method.Parameters.Length == 0) return false;

                var type = method.Parameters[0].Type;
                return type.SpecialType == SpecialType.System_Collections_IEnumerable
                    || type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;
            }

            // `base.Tick()` runs the base implementation, whatever overrides it.
            private static bool IsBaseCall(InvocationExpressionSyntax invocation) =>
                invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax };

            private IEnumerable<IMethodSymbol> GetTargets(IMethodSymbol target, bool isBaseCall)
            {
                target = Normalize(target);
                yield return target;

                if (isBaseCall) yield break;

                var dispatches = target.ContainingType?.TypeKind == TypeKind.Interface
                    ? !target.IsStatic && (target.IsAbstract || target.IsVirtual)
                    : target.IsVirtual || target.IsAbstract || target.IsOverride;
                if (!dispatches) yield break;

                var targets = _dispatchTargets.GetOrAdd(target, FindDispatchTargets);

                foreach (var dispatched in targets)
                    yield return dispatched;
            }

            // Every override of a virtual or abstract method, or every implementation of an interface
            // method, declared in this compilation.
            private List<IMethodSymbol> FindDispatchTargets(IMethodSymbol target)
            {
                var targets = new List<IMethodSymbol>();
                var isInterfaceMethod = target.ContainingType.TypeKind == TypeKind.Interface;

                foreach (var type in _sourceTypes.Value)
                {
                    _cancellationToken.ThrowIfCancellationRequested();

                    if (isInterfaceMethod)
                    {
                        if (type.TypeKind == TypeKind.Interface) continue;

                        foreach (var implemented in type.AllInterfaces)
                        {
                            if (!SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, target.ContainingType)) continue;

                            foreach (var member in implemented.GetMembers(target.Name).OfType<IMethodSymbol>())
                            {
                                if (!SymbolEqualityComparer.Default.Equals(member.OriginalDefinition, target)) continue;

                                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                                    targets.Add(Normalize(implementation));
                            }
                        }

                        continue;
                    }

                    foreach (var member in type.GetMembers(target.Name).OfType<IMethodSymbol>())
                    {
                        for (var overridden = member.IsOverride ? member.OverriddenMethod : null;
                             overridden != null;
                             overridden = overridden.OverriddenMethod)
                        {
                            if (!SymbolEqualityComparer.Default.Equals(Normalize(overridden), target)) continue;

                            targets.Add(Normalize(member));
                            break;
                        }
                    }
                }

                return targets;
            }

            private List<INamedTypeSymbol> CollectSourceTypes()
            {
                var types = new List<INamedTypeSymbol>();
                var namespaces = new Stack<INamespaceSymbol>();
                namespaces.Push(_compilation.Assembly.GlobalNamespace);

                while (namespaces.Count > 0)
                {
                    var ns = namespaces.Pop();
                    foreach (var nested in ns.GetNamespaceMembers()) namespaces.Push(nested);
                    foreach (var type in ns.GetTypeMembers()) AddWithNestedTypes(type, types);
                }

                return types;
            }

            private static void AddWithNestedTypes(INamedTypeSymbol type, List<INamedTypeSymbol> types)
            {
                types.Add(type);
                foreach (var nested in type.GetTypeMembers()) AddWithNestedTypes(nested, types);
            }

            // A method with a body in source, in a type no option excludes and not a pool, that is not
            // an iterator and not Burst-compiled.
            private bool IsWalkable(IMethodSymbol method)
            {
                if (method.IsAbstract || method.ContainingType == null) return false;
                if (method.ContainingType.Name.EndsWith(PoolSuffix, System.StringComparison.Ordinal)) return false;

                var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
                if (reference == null) return false;

                var declared = method.PartialImplementationPart ?? method;
                if (declared.DeclaringSyntaxReferences.Any(r => ContainsYield(r.GetSyntax(_cancellationToken)))) return false;

                var options = _detector.GetOptions(reference.SyntaxTree, _analyzerOptions);
                if (options.IsExcludedType(method.ContainingType)) return false;

                return !_detector.IsBurstCompiled(method);
            }

            // `yield` in the method's own body, not in a lambda or local function inside it.
            private static bool ContainsYield(SyntaxNode declaration) =>
                declaration
                    .DescendantNodes(node => node == declaration ||
                                             node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                    .OfType<YieldStatementSyntax>()
                    .Any();

#pragma warning disable RS1030 // The call graph needs the bodies of methods in other files than the one analyzed.
            private SemanticModel GetSemanticModel(SyntaxTree tree) =>
                _semanticModels.GetOrAdd(tree, t => _compilation.GetSemanticModel(t));
#pragma warning restore RS1030
        }
    }
}
