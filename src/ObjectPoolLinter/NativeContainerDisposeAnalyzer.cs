using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace ObjectPoolLinter
{
    // OPL007: a NativeArray<T>, NativeList<T>, NativeHashMap<TKey, TValue> or other native container
    // that is allocated and never disposed. The memory lives outside the managed heap, so the garbage
    // collector never frees it; Unity only logs the leak, and inside Update it leaks every frame.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NativeContainerDisposeAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL007";

        private const string Category = "Reliability";
        private static readonly LocalizableString Title = "Native container not disposed";
        private static readonly LocalizableString MessageFormat = "'{0}' allocated with Allocator.{1} {2}. {3}.";
        private static readonly LocalizableString Description = "Native containers (NativeArray<T>, NativeList<T>, NativeHashMap<TKey, TValue>, ...) allocate unmanaged memory that the garbage collector never frees. A container allocated with Allocator.TempJob or Allocator.Persistent must be disposed on every path out of the method that owns it, or, when it is kept in a field, by the type that owns the field.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL007.md";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri
        );

        private const string NativeContainerMetadataName = "Unity.Collections.LowLevel.Unsafe.NativeContainerAttribute";
        private const string AllocatorMetadataName = "Unity.Collections.Allocator";
        private const string DeallocateOnJobCompletionMetadataName = "Unity.Collections.DeallocateOnJobCompletionAttribute";

        // Allocator.Temp memory is released by Unity at the end of the frame (or of the job), so it
        // needs no Dispose. None and Invalid allocate nothing.
        private static readonly ImmutableHashSet<string> SelfReleasingAllocators =
            ImmutableHashSet.Create(System.StringComparer.Ordinal, "Temp", "None", "Invalid");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var compilation = context.Compilation;

            var nativeContainer = compilation.GetTypeByMetadataName(NativeContainerMetadataName);
            var allocator = compilation.GetTypeByMetadataName(AllocatorMetadataName);
            if (nativeContainer == null || allocator == null) return;

            var analyzer = new CompilationAnalyzer(
                nativeContainer,
                allocator,
                compilation.GetTypeByMetadataName(DeallocateOnJobCompletionMetadataName));

            context.RegisterOperationAction(analyzer.AnalyzeLocalCreation, OperationKind.ObjectCreation);
            context.RegisterSymbolStartAction(analyzer.OnTypeStart, SymbolKind.NamedType);
        }

        private enum Target
        {
            None,
            Local,
            Member,
            Discarded,
        }

        private sealed class CompilationAnalyzer
        {
            private readonly INamedTypeSymbol _nativeContainer;
            private readonly INamedTypeSymbol _allocator;
            private readonly INamedTypeSymbol? _deallocateOnJobCompletion;

            internal CompilationAnalyzer(INamedTypeSymbol nativeContainer, INamedTypeSymbol allocator, INamedTypeSymbol? deallocateOnJobCompletion)
            {
                _nativeContainer = nativeContainer;
                _allocator = allocator;
                _deallocateOnJobCompletion = deallocateOnJobCompletion;
            }

            // A container held in a local, or thrown away as soon as it is made: it has to be disposed
            // before the method it lives in returns.
            internal void AnalyzeLocalCreation(OperationAnalysisContext context)
            {
                var creation = (IObjectCreationOperation)context.Operation;
                if (!TryGetAllocator(creation, out var allocator)) return;

                var target = GetTarget(creation, out var symbol);
                if (target == Target.Discarded)
                {
                    Report(context.ReportDiagnostic, creation, allocator, "in '" + context.ContainingSymbol.Name + "' is never disposed", "Keep it in a variable and call Dispose() on it");
                    return;
                }

                if (target != Target.Local) return;

                var local = (ILocalSymbol)symbol!;
                if (IsUsingLocal(local)) return;

                var function = GetEnclosingFunction(creation);
                if (function == null || IsCapturedOutside(function, local)) return;

                var graph = GetGraph(context, function);
                if (graph == null) return;

                var result = new DisposeWalker(graph, local, creation.Syntax).Walk();
                if (result == Leak.None) return;

                var name = function is ILocalFunctionOperation localFunction ? localFunction.Symbol.Name : context.ContainingSymbol.Name;
                if (result == Leak.Never)
                    Report(context.ReportDiagnostic, creation, allocator, "in '" + name + "' is never disposed", "Call Dispose() on it, or declare it with 'using'");
                else
                    Report(context.ReportDiagnostic, creation, allocator, "in '" + name + "' is not disposed on every path out of the method", "Call Dispose() on it before each return, or declare it with 'using'");
            }

            // A container stored in a field or auto-property: some member of the type (OnDestroy,
            // OnDisable, Dispose) has to dispose it. Collected per type and checked once the whole
            // type, every partial declaration included, has been seen.
            internal void OnTypeStart(SymbolStartAnalysisContext context)
            {
                var type = (INamedTypeSymbol)context.Symbol;
                if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct) return;

                var created = new ConcurrentBag<(ISymbol Member, IObjectCreationOperation Creation, string Allocator)>();
                var released = new ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);

                context.RegisterOperationAction(operationContext =>
                {
                    var creation = (IObjectCreationOperation)operationContext.Operation;
                    if (!TryGetAllocator(creation, out var allocator)) return;
                    if (GetTarget(creation, out var target) != Target.Member) return;

                    var member = target!.OriginalDefinition;
                    if (!SymbolEqualityComparer.Default.Equals(member.ContainingType.OriginalDefinition, type)) return;
                    if (_deallocateOnJobCompletion != null && HasAttribute(member, _deallocateOnJobCompletion)) return;

                    created.Add((member, creation, allocator));
                }, OperationKind.ObjectCreation);

                context.RegisterOperationAction(operationContext =>
                {
                    var member = GetReferencedMember(operationContext.Operation);
                    if (member != null && ReleasesValue(operationContext.Operation))
                        released[member] = true;
                }, OperationKind.FieldReference, OperationKind.PropertyReference);

                context.RegisterSymbolEndAction(endContext =>
                {
                    foreach (var (member, creation, allocator) in created)
                    {
                        if (released.ContainsKey(member)) continue;

                        Report(endContext.ReportDiagnostic, creation, allocator,
                            "into '" + member.Name + "' is never disposed by '" + type.Name + "'",
                            "Call Dispose() on it in OnDestroy, OnDisable or Dispose");
                    }
                });
            }

            // True for a native container made with an allocator Unity does not release on its own.
            // An allocator that is not a constant (a parameter, a field) cannot be judged and is skipped.
            private bool TryGetAllocator(IObjectCreationOperation creation, out string allocator)
            {
                allocator = string.Empty;

                if (creation.Type is not INamedTypeSymbol type || !HasAttribute(type.OriginalDefinition, _nativeContainer)) return false;

                foreach (var argument in creation.Arguments)
                {
                    var name = GetAllocatorName(argument.Value);
                    if (name == null) continue;

                    allocator = name;
                    return !SelfReleasingAllocators.Contains(name);
                }

                return false;
            }

            // `Allocator.TempJob`, also when converted to an AllocatorManager.AllocatorHandle.
            private string? GetAllocatorName(IOperation value)
            {
                while (value is IConversionOperation conversion) value = conversion.Operand;

                if (!SymbolEqualityComparer.Default.Equals(value.Type, _allocator)) return null;

                if (value is IFieldReferenceOperation { Field.HasConstantValue: true } field) return field.Field.Name;

                if (!value.ConstantValue.HasValue) return null;

                return _allocator.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value.ConstantValue.Value))?.Name;
            }

            // Where the new container ends up. Anything else (an argument, a return value, a member of
            // another object such as a job's field in an object initializer) hands it to code that
            // this rule does not follow.
            private static Target GetTarget(IObjectCreationOperation creation, out ISymbol? symbol)
            {
                symbol = null;

                IOperation current = creation;
                while (current.Parent is IConversionOperation or IParenthesizedOperation) current = current.Parent;

                switch (current.Parent)
                {
                    case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                        symbol = declarator.Symbol;
                        return Target.Local;

                    case IFieldInitializerOperation initializer when initializer.InitializedFields.Length == 1:
                        symbol = initializer.InitializedFields[0];
                        return Target.Member;

                    case IPropertyInitializerOperation initializer when initializer.InitializedProperties.Length == 1:
                        symbol = initializer.InitializedProperties[0];
                        return Target.Member;

                    case ISimpleAssignmentOperation assignment when assignment.Value == current:
                        switch (assignment.Target)
                        {
                            case ILocalReferenceOperation local:
                                symbol = local.Local;
                                return Target.Local;
                            case IMemberReferenceOperation member when IsOwnMember(member):
                                symbol = member.Member;
                                return member.Member is IFieldSymbol or IPropertySymbol ? Target.Member : Target.None;
                            default:
                                return Target.None;
                        }

                    case IExpressionStatementOperation:
                        return Target.Discarded;

                    default:
                        return Target.None;
                }
            }

            // `_buffer`, `this._buffer`, `Owner.s_buffer` - not `job.Data` or an object initializer's member.
            private static bool IsOwnMember(IMemberReferenceOperation member)
            {
                return member.Instance == null ||
                       member.Instance is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance };
            }

            // `using var buffer = ...;` and `using (var buffer = ...)`.
            private static bool IsUsingLocal(ILocalSymbol local)
            {
                foreach (var reference in local.DeclaringSyntaxReferences)
                {
                    var declaration = reference.GetSyntax().Parent;
                    switch (declaration?.Parent)
                    {
                        case LocalDeclarationStatementSyntax statement when statement.UsingKeyword.RawKind != 0:
                        case UsingStatementSyntax:
                            return true;
                    }
                }

                return false;
            }

            // The method or local function the creation runs in; null inside a lambda, which this rule
            // does not follow.
            private static IOperation? GetEnclosingFunction(IOperation operation)
            {
                for (var current = operation.Parent; current != null; current = current.Parent)
                {
                    switch (current)
                    {
                        case IAnonymousFunctionOperation:
                            return null;
                        case ILocalFunctionOperation:
                            return current;
                        case IMethodBodyOperation:
                        case IConstructorBodyOperation:
                            return current;
                    }
                }

                return null;
            }

            // A local a lambda or local function reads can be disposed there, out of this method's
            // control flow; leave it alone.
            private static bool IsCapturedOutside(IOperation function, ILocalSymbol local)
            {
                foreach (var reference in function.Descendants().OfType<ILocalReferenceOperation>())
                {
                    if (!SymbolEqualityComparer.Default.Equals(reference.Local, local)) continue;

                    for (var current = reference.Parent; current != null && current != function; current = current.Parent)
                    {
                        if (current is IAnonymousFunctionOperation or ILocalFunctionOperation) return true;
                    }
                }

                return false;
            }

            private static ControlFlowGraph? GetGraph(OperationAnalysisContext context, IOperation function)
            {
                var graph = context.GetControlFlowGraph();
                return function is ILocalFunctionOperation localFunction ? FindLocalFunctionGraph(graph, localFunction.Symbol) : graph;
            }

            private static ControlFlowGraph? FindLocalFunctionGraph(ControlFlowGraph graph, IMethodSymbol localFunction)
            {
                foreach (var candidate in graph.LocalFunctions)
                {
                    var nested = graph.GetLocalFunctionControlFlowGraph(candidate);
                    if (SymbolEqualityComparer.Default.Equals(candidate, localFunction)) return nested;

                    var found = FindLocalFunctionGraph(nested, localFunction);
                    if (found != null) return found;
                }

                return null;
            }

            private static ISymbol? GetReferencedMember(IOperation operation)
            {
                return operation switch
                {
                    IFieldReferenceOperation field => field.Field.OriginalDefinition,
                    IPropertyReferenceOperation property => property.Property.OriginalDefinition,
                    _ => null,
                };
            }

            // `_buffer.Dispose()`, `_buffer.Dispose(handle)`, or the member handed to the project's
            // own code (`DisposeAll(_buffer)`, `Release(ref _buffer)`), which may dispose it.
            private static bool ReleasesValue(IOperation reference)
            {
                var parent = reference.Parent;
                while (parent is IConversionOperation or IParenthesizedOperation)
                {
                    reference = parent;
                    parent = parent.Parent;
                }

                switch (parent)
                {
                    case IInvocationOperation invocation when invocation.Instance == reference:
                        return IsDispose(invocation.TargetMethod);
                    case IArgumentOperation argument:
                        return argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out || IsDeclaredInSource(argument.Parent);
                    default:
                        return false;
                }
            }

            private static void Report(System.Action<Diagnostic> report, IObjectCreationOperation creation, string allocator, string problem, string advice)
            {
                var type = creation.Type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                report(Diagnostic.Create(Rule, creation.Syntax.GetLocation(), type, allocator, problem, advice));
            }

            private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
            {
                return symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
            }
        }

        private static bool IsDispose(IMethodSymbol method) => method.Name == "Dispose";

        private static bool IsDeclaredInSource(IOperation? call)
        {
            ISymbol? method = call switch
            {
                IInvocationOperation invocation => invocation.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                _ => null,
            };

            return method != null && method.Locations.Any(l => l.IsInSource);
        }

        private enum Leak
        {
            None,
            SomePaths,
            Never,
        }

        // Walks the control flow graph forward from the assignment that stores the new container in
        // `local`, looking for a path to the end of the method on which the container is neither
        // disposed nor handed on. Paths that end in a throw do not count: the exception is the bug.
        private sealed class DisposeWalker
        {
            private readonly ControlFlowGraph _graph;
            private readonly ILocalSymbol _local;
            private readonly SyntaxNode _creationSyntax;

            internal DisposeWalker(ControlFlowGraph graph, ILocalSymbol local, SyntaxNode creationSyntax)
            {
                _graph = graph;
                _local = local;
                _creationSyntax = creationSyntax;
            }

            internal Leak Walk()
            {
                if (!TryFindCreation(out var creationBlock, out var creationIndex)) return Leak.None;

                var disposedSomewhere = IsDisposedAnywhere();

                var visited = new HashSet<int>();
                var pending = new Stack<BasicBlock>();

                // The rest of the block after the assignment.
                if (Scan(creationBlock, creationIndex + 1, creationBlock.Operations.Length, includeBranchValue: true) != Effect.None)
                    return Leak.None;
                Follow(creationBlock, pending);

                while (pending.Count > 0)
                {
                    var block = pending.Pop();
                    if (!visited.Add(block.Ordinal)) continue;

                    if (block.Kind == BasicBlockKind.Exit) return ToLeak(disposedSomewhere);

                    if (block == creationBlock)
                    {
                        // Back at the allocation, e.g. the next turn of a loop: the old container is
                        // overwritten without having been disposed.
                        if (Scan(block, 0, creationIndex, includeBranchValue: false) == Effect.None)
                            return ToLeak(disposedSomewhere);
                        continue;
                    }

                    if (Scan(block, 0, block.Operations.Length, includeBranchValue: true) != Effect.None) continue;
                    Follow(block, pending);
                }

                return Leak.None;
            }

            private static Leak ToLeak(bool disposedSomewhere) => disposedSomewhere ? Leak.SomePaths : Leak.Never;

            // Picks the message: a container disposed on some paths was only missed on the others.
            private bool IsDisposedAnywhere()
            {
                foreach (var block in _graph.Blocks)
                {
                    var roots = block.BranchValue == null ? block.Operations : block.Operations.Add(block.BranchValue);
                    foreach (var invocation in roots.SelectMany(root => root.DescendantsAndSelf()).OfType<IInvocationOperation>())
                    {
                        if (IsDispose(invocation.TargetMethod) && invocation.Instance != null && IsLocal(Unwrap(invocation.Instance)))
                            return true;
                    }
                }

                return false;
            }

            // Queues the successors of `block` that a path out of the method can take.
            private void Follow(BasicBlock block, Stack<BasicBlock> pending)
            {
                foreach (var branch in new[] { block.ConditionalSuccessor, block.FallThroughSuccessor })
                {
                    if (branch?.Destination == null) continue;

                    switch (branch.Semantics)
                    {
                        case ControlFlowBranchSemantics.Throw:
                        case ControlFlowBranchSemantics.Rethrow:
                        case ControlFlowBranchSemantics.Error:
                        case ControlFlowBranchSemantics.ProgramTermination:
                        case ControlFlowBranchSemantics.StructuredExceptionHandling:
                            continue;
                    }

                    // `finally` blocks run on the way out of a try; one that disposes covers this path.
                    if (branch.FinallyRegions.Any(DisposesInRegion)) continue;

                    pending.Push(branch.Destination);
                }
            }

            private bool DisposesInRegion(ControlFlowRegion region)
            {
                for (var ordinal = region.FirstBlockOrdinal; ordinal <= region.LastBlockOrdinal; ordinal++)
                {
                    var block = _graph.Blocks[ordinal];
                    if (Scan(block, 0, block.Operations.Length, includeBranchValue: true) == Effect.Disposed) return true;
                }

                return false;
            }

            private bool TryFindCreation(out BasicBlock block, out int index)
            {
                foreach (var candidate in _graph.Blocks)
                {
                    for (var i = 0; i < candidate.Operations.Length; i++)
                    {
                        var operation = candidate.Operations[i];
                        if (operation.DescendantsAndSelf().Any(o => o is IObjectCreationOperation && o.Syntax == _creationSyntax))
                        {
                            block = candidate;
                            index = i;
                            return true;
                        }
                    }
                }

                block = null!;
                index = -1;
                return false;
            }

            private enum Effect
            {
                None,
                Disposed,
                HandedOn,
            }

            // What operations [from, to) of the block, and its branch value, do to the container.
            private Effect Scan(BasicBlock block, int from, int to, bool includeBranchValue)
            {
                for (var i = from; i < to; i++)
                {
                    var effect = ScanOperation(block.Operations[i]);
                    if (effect != Effect.None) return effect;
                }

                if (!includeBranchValue || block.BranchValue == null) return Effect.None;

                // `return buffer;` - the caller owns it now.
                if (IsLocal(Unwrap(block.BranchValue))) return Effect.HandedOn;

                return ScanOperation(block.BranchValue);
            }

            private Effect ScanOperation(IOperation root)
            {
                foreach (var operation in root.DescendantsAndSelf())
                {
                    if (!IsLocal(operation)) continue;

                    var effect = Classify(operation);
                    if (effect != Effect.None) return effect;
                }

                return Effect.None;
            }

            private Effect Classify(IOperation reference)
            {
                var parent = reference.Parent;
                while (parent is IConversionOperation or IParenthesizedOperation)
                {
                    reference = parent;
                    parent = parent.Parent;
                }

                switch (parent)
                {
                    case IInvocationOperation invocation when invocation.Instance == reference:
                        return IsDispose(invocation.TargetMethod) ? Effect.Disposed : Effect.None;

                    // Passed by reference, or to the project's own code, which may dispose it.
                    case IArgumentOperation argument:
                        return argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out || IsDeclaredInSource(argument.Parent)
                            ? Effect.HandedOn
                            : Effect.None;

                    // Stored somewhere else (a field, another local, a job's field), or the variable
                    // itself overwritten.
                    case ISimpleAssignmentOperation:
                        return Effect.HandedOn;

                    case IFlowCaptureOperation:
                    case IReturnOperation:
                        return Effect.HandedOn;

                    default:
                        return Effect.None;
                }
            }

            private bool IsLocal(IOperation operation)
            {
                return operation is ILocalReferenceOperation reference && SymbolEqualityComparer.Default.Equals(reference.Local, _local);
            }

            private static IOperation Unwrap(IOperation operation)
            {
                while (operation is IConversionOperation conversion) operation = conversion.Operand;
                return operation;
            }
        }
    }
}
