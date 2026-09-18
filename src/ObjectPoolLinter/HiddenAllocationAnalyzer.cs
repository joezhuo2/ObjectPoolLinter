using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ObjectPoolLinter
{
    // OPL002: allocations that have no `new` in the source. OPL001 owns `new` expressions (including a
    // struct boxed as it is created) and Instantiate; this rule owns everything the compiler or the
    // base class library allocates on the code's behalf.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HiddenAllocationAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL002";

        private const string Category = "Performance";
        private static readonly LocalizableString Title = "Hidden allocation in hot path";
        private static readonly LocalizableString MessageFormat = "'{0}' allocates inside the frequently-called method '{1}'. Move it out of the method or cache the result.";
        private static readonly LocalizableString Description = "String concatenation and interpolation, string.Concat, string.Format, StringBuilder.ToString, capturing lambdas, method-group delegates, implicit params arrays, LINQ and boxing all allocate without a 'new' in the source. Inside frequently-invoked Unity methods (such as Update) that garbage builds up every frame.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL002.md";

        // Info by default: string formatting and LINQ are common in per-frame code that is not
        // performance-critical, so a warning would bury OPL001. Raise it from .editorconfig.
        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri
        );

        // C# 11 caches delegates for static method groups. LanguageVersion.CSharp11 is 1100; the enum
        // member does not exist in the Roslyn 3.8 the analyzer builds against.
        private const LanguageVersion CSharp11 = (LanguageVersion)1100;

        // Diagnostic property naming the kind of allocation: string, delegate, params, linq or boxing.
        internal const string AllocationKindProperty = "AllocationKind";

        private static readonly string[] KindNames = { "string", "delegate", "params", "linq", "boxing" };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var hotPaths = HotPathDetector.Create(context.Compilation);
            if (hotPaths == null) return;

            var analyzer = new CompilationAnalyzer(
                hotPaths,
                context.Compilation.GetTypeByMetadataName("System.Linq.Enumerable"),
                context.Compilation.GetTypeByMetadataName("System.Text.StringBuilder"));

            context.RegisterOperationAction(analyzer.AnalyzeBinary, OperationKind.Binary);
            context.RegisterOperationAction(analyzer.AnalyzeCompoundAssignment, OperationKind.CompoundAssignment);
            context.RegisterOperationAction(analyzer.AnalyzeInterpolatedString, OperationKind.InterpolatedString);
            context.RegisterOperationAction(analyzer.AnalyzeDelegateCreation, OperationKind.DelegateCreation);
            context.RegisterOperationAction(analyzer.AnalyzeArgument, OperationKind.Argument);
            context.RegisterOperationAction(analyzer.AnalyzeInvocation, OperationKind.Invocation);
            context.RegisterOperationAction(analyzer.AnalyzeTranslatedQuery, OperationKind.TranslatedQuery);
            context.RegisterOperationAction(analyzer.AnalyzeConversion, OperationKind.Conversion);
        }

        private sealed class CompilationAnalyzer
        {
            private readonly HotPathDetector _hotPaths;
            private readonly INamedTypeSymbol? _enumerable;
            private readonly INamedTypeSymbol? _stringBuilder;

            internal CompilationAnalyzer(HotPathDetector hotPaths, INamedTypeSymbol? enumerable, INamedTypeSymbol? stringBuilder)
            {
                _hotPaths = hotPaths;
                _enumerable = enumerable;
                _stringBuilder = stringBuilder;
            }

            // `a + b` on strings. Only the outermost `+` of a chain is reported, since `a + b + c`
            // compiles to one string.Concat call. Constant-folded concatenations allocate nothing.
            internal void AnalyzeBinary(OperationAnalysisContext context)
            {
                var binary = (IBinaryOperation)context.Operation;
                if (!IsStringConcatenation(binary) || binary.ConstantValue.HasValue) return;
                if (binary.Parent is IBinaryOperation parent && IsStringConcatenation(parent)) return;

                Report(context, binary.Syntax, AllocationKind.String, "string concatenation");
            }

            internal void AnalyzeCompoundAssignment(OperationAnalysisContext context)
            {
                var assignment = (ICompoundAssignmentOperation)context.Operation;
                if (assignment.OperatorKind != BinaryOperatorKind.Add) return;
                if (assignment.Type?.SpecialType != SpecialType.System_String) return;

                Report(context, assignment.Syntax, AllocationKind.String, "string concatenation");
            }

            internal void AnalyzeInterpolatedString(OperationAnalysisContext context)
            {
                var interpolated = (IInterpolatedStringOperation)context.Operation;
                if (interpolated.ConstantValue.HasValue) return;
                if (!interpolated.Parts.Any(part => part is IInterpolationOperation)) return;

                Report(context, interpolated.Syntax, AllocationKind.String, "string interpolation");
            }

            // A lambda that captures nothing is cached in a static field by the compiler and allocates
            // once. One that captures a local, a parameter or `this` allocates a delegate (and a closure
            // for locals) every time the expression runs. A method group allocates a delegate every
            // time, except a static one under C# 11 or later.
            internal void AnalyzeDelegateCreation(OperationAnalysisContext context)
            {
                var creation = (IDelegateCreationOperation)context.Operation;

                // `new Action(Spawn)` is a `new` expression, already reported by OPL001.
                if (creation.Syntax is BaseObjectCreationExpressionSyntax) return;

                switch (creation.Target)
                {
                    case IAnonymousFunctionOperation lambda:
                        var captures = GetCapturedNames(lambda);
                        if (captures.Count == 0) return;
                        Report(context, lambda.Syntax, AllocationKind.Delegate, "lambda capturing " + string.Join(", ", captures));
                        break;

                    case IMethodReferenceOperation methodReference:
                        var method = methodReference.Method;
                        if (method.IsStatic && creation.Syntax.SyntaxTree.Options is CSharpParseOptions { LanguageVersion: >= CSharp11 })
                            return;
                        Report(context, methodReference.Syntax, AllocationKind.Delegate, "delegate for " + method.Name + "()");
                        break;
                }
            }

            // `Log("{0} {1}", a, b)` against `Log(string, params object[])` allocates the array. An empty
            // expansion uses Array.Empty<T>() and allocates nothing.
            internal void AnalyzeArgument(OperationAnalysisContext context)
            {
                var argument = (IArgumentOperation)context.Operation;
                if (argument.ArgumentKind != ArgumentKind.ParamArray) return;
                if (argument.Value is not IArrayCreationOperation { Initializer: { ElementValues.Length: > 0 } } array) return;

                var calleeName = argument.Parent switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod.Name,
                    IObjectCreationOperation objectCreation => objectCreation.Constructor?.ContainingType.Name,
                    _ => null,
                };
                if (calleeName == null || array.Type == null) return;

                // `string.Format("{0} {1} {2} {3}", a, b, c, d)`: the array is part of the formatting
                // call already reported.
                if (argument.Parent is IInvocationOperation stringCall && IsStringFormattingCall(stringCall.TargetMethod)) return;

                var arrayType = array.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                Report(context, argument.Parent!.Syntax, AllocationKind.Params, "params " + arrayType + " for " + calleeName + "()");
            }

            internal void AnalyzeInvocation(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                if (invocation.IsImplicit) return;

                if (IsLinqCall(invocation))
                {
                    if (IsReceiverOfLinqCall(invocation)) return;
                    Report(context, invocation.Syntax, AllocationKind.Linq, "LINQ " + DescribeLinqChain(invocation));
                    return;
                }

                // Explicit string building. `a + b` compiles to string.Concat too, but that call is not in
                // the operation tree, so it is reported once, by AnalyzeBinary.
                var stringCall = DescribeStringCall(invocation.TargetMethod);
                if (stringCall != null)
                {
                    Report(context, invocation.Syntax, AllocationKind.String, stringCall);
                    return;
                }

                // A struct that does not override GetHashCode, Equals or ToString runs the object or
                // ValueType implementation, which needs a boxed receiver. Enum.HasFlag boxes on Mono.
                var method = invocation.TargetMethod;
                if (method.IsStatic || invocation.Instance?.Type is not { IsValueType: true } receiverType) return;
                if (receiverType is ITypeParameterSymbol) return;

                var declaringType = method.ContainingType?.SpecialType;
                if (declaringType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum)
                    Report(context, invocation.Syntax, AllocationKind.Boxing, "boxing " + Display(receiverType) + " for " + method.Name + "()");
            }

            internal void AnalyzeTranslatedQuery(OperationAnalysisContext context)
            {
                var query = (ITranslatedQueryOperation)context.Operation;
                if (IsReceiverOfLinqCall(query)) return;

                Report(context, query.Syntax, AllocationKind.Linq, "LINQ query");
            }

            // Boxing of an existing value: `object o = count;`, `Debug.Log(count)`. A struct boxed as it
            // is created is OPL001's; boxing inside a concatenation or an interpolation hole is part of
            // the string allocation already reported, and so is boxing an argument to string.Concat or
            // string.Format.
            internal void AnalyzeConversion(OperationAnalysisContext context)
            {
                var conversion = (IConversionOperation)context.Operation;
                if (!conversion.GetConversion().IsBoxing || conversion.Type == null) return;

                var operand = conversion.Operand;
                if (operand.Type == null || operand is IObjectCreationOperation) return;
                if (operand.Type is ITypeParameterSymbol { HasValueTypeConstraint: false }) return;
                if (operand.ConstantValue is { HasValue: true, Value: null }) return;

                if (conversion.Parent is IInterpolationOperation) return;
                if (conversion.Parent is IBinaryOperation parent && IsStringConcatenation(parent)) return;
                if (conversion.Parent is ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add } compound &&
                    compound.Type?.SpecialType == SpecialType.System_String) return;
                if (IsArgumentToStringFormattingCall(conversion)) return;

                Report(context, conversion.Syntax, AllocationKind.Boxing, "boxing " + Display(operand.Type) + " to " + Display(conversion.Type));
            }

            private bool IsLinqCall(IInvocationOperation invocation)
            {
                return _enumerable != null
                    && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, _enumerable)
                    && invocation.TargetMethod.Name != "Empty";
            }

            // True when the operation is the source of an enclosing LINQ call, so the chain is reported
            // once, on its outermost call.
            private bool IsReceiverOfLinqCall(IOperation operation)
            {
                var current = operation;
                while (current.Parent is IConversionOperation { IsImplicit: true })
                    current = current.Parent;

                return current.Parent is IArgumentOperation { Parameter: { Ordinal: 0 } } argument
                    && argument.Parent is IInvocationOperation outer
                    && !outer.IsImplicit
                    && outer.TargetMethod.IsExtensionMethod
                    && IsLinqCall(outer);
            }

            private string DescribeLinqChain(IInvocationOperation outermost)
            {
                var links = new List<string>();
                IOperation? current = outermost;

                while (current != null)
                {
                    if (current is ITranslatedQueryOperation)
                    {
                        links.Add("query");
                        break;
                    }

                    if (current is not IInvocationOperation invocation || invocation.IsImplicit || !IsLinqCall(invocation))
                        break;

                    links.Add(invocation.TargetMethod.Name + "()");
                    if (!invocation.TargetMethod.IsExtensionMethod || invocation.Arguments.Length == 0) break;

                    current = invocation.Arguments[0].Value;
                    while (current is IConversionOperation { IsImplicit: true } conversion)
                        current = conversion.Operand;
                }

                links.Reverse();
                return string.Join(".", links);
            }

            // Names of the variables from outside the lambda that it reads or writes, in order of first
            // use, with `this` for any instance member access.
            private static List<string> GetCapturedNames(IAnonymousFunctionOperation lambda)
            {
                var owners = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { lambda.Symbol };
                foreach (var nested in lambda.Body.Descendants().OfType<IAnonymousFunctionOperation>())
                    owners.Add(nested.Symbol);

                var names = new List<string>();
                foreach (var operation in lambda.Body.Descendants())
                {
                    string? name = operation switch
                    {
                        // ImplicitReceiver is the object an initializer is filling in, not `this`.
                        IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } => "this",
                        ILocalReferenceOperation local when !owners.Contains(local.Local.ContainingSymbol) => local.Local.Name,
                        IParameterReferenceOperation parameter when !owners.Contains(parameter.Parameter.ContainingSymbol) => parameter.Parameter.Name,
                        _ => null,
                    };

                    if (name != null && !names.Contains(name)) names.Add(name);
                }

                return names;
            }

            // "string.Concat()", "string.Format()" or "StringBuilder.ToString()" when the method is one of
            // those; null otherwise.
            private string? DescribeStringCall(IMethodSymbol method)
            {
                if (IsStringFormattingCall(method)) return "string." + method.Name + "()";

                if (method.Name == "ToString" && _stringBuilder != null &&
                    SymbolEqualityComparer.Default.Equals(method.ContainingType, _stringBuilder))
                    return "StringBuilder.ToString()";

                return null;
            }

            private static bool IsStringFormattingCall(IMethodSymbol method)
            {
                return method.IsStatic
                    && method.ContainingType?.SpecialType == SpecialType.System_String
                    && method.Name is "Concat" or "Format";
            }

            // The value is passed to string.Concat or string.Format, directly or as an element of the
            // params array the compiler builds for the call.
            private static bool IsArgumentToStringFormattingCall(IOperation operation)
            {
                var parent = operation.Parent;
                if (parent is IArrayInitializerOperation { Parent: IArrayCreationOperation { IsImplicit: true } array })
                    parent = array.Parent;

                return parent is IArgumentOperation { Parent: IInvocationOperation invocation }
                    && IsStringFormattingCall(invocation.TargetMethod);
            }

            private static bool IsStringConcatenation(IBinaryOperation binary)
            {
                return binary.OperatorKind == BinaryOperatorKind.Add
                    && binary.Type?.SpecialType == SpecialType.System_String;
            }

            private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            // The kind's own severity from .editorconfig (`object_pool_linter.linq_severity = warning`)
            // replaces the rule's Info. `dotnet_diagnostic.OPL002.severity`, when set, is applied by the
            // compiler afterwards and wins, except that a kind set to `none` is never reported.
            private void Report(OperationAnalysisContext context, SyntaxNode node, AllocationKind kind, string allocation)
            {
                var semanticModel = context.Operation.SemanticModel;
                if (semanticModel == null) return;

                var severity = _hotPaths.GetOptions(node.SyntaxTree, context.Options).GetSeverity(kind);
                if (severity == ReportDiagnostic.Suppress) return;

                if (!_hotPaths.TryGetHotPathMethod(node, semanticModel, context.Options, context.CancellationToken, out var methodName))
                    return;

                var properties = ImmutableDictionary<string, string?>.Empty.Add(AllocationKindProperty, KindNames[(int)kind]);
                var effectiveSeverity = severity switch
                {
                    ReportDiagnostic.Error => DiagnosticSeverity.Error,
                    ReportDiagnostic.Warn => DiagnosticSeverity.Warning,
                    ReportDiagnostic.Info => DiagnosticSeverity.Info,
                    ReportDiagnostic.Hidden => DiagnosticSeverity.Hidden,
                    _ => Rule.DefaultSeverity,
                };

                context.ReportDiagnostic(Diagnostic.Create(
                    Rule, node.GetLocation(), effectiveSeverity, additionalLocations: null, properties, allocation, methodName));
            }
        }
    }
}
