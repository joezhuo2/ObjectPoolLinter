using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ObjectPoolLinter
{
    // OPL003: Unity engine APIs that hand back freshly allocated memory on every call.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UnityApiAllocationAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL003";

        private const string Category = "Performance";
        private static readonly LocalizableString Title = "Allocating Unity API in hot path";
        private static readonly LocalizableString MessageFormat = "'{0}' returns a new '{1}' on every call inside the frequently-called method '{2}'. Use a non-allocating overload or cache the result.";
        private static readonly LocalizableString Description = "Unity engine methods and properties that return arrays copy them out of native memory on every call, and Object.name, tag, Application.dataPath and a few other members build a new string or object each time. Inside frequently-invoked Unity methods (such as Update) that garbage builds up every frame.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL003.md";

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

            var analyzer = new CompilationAnalyzer(hotPaths);

            context.RegisterOperationAction(analyzer.AnalyzeInvocation, OperationKind.Invocation);
            context.RegisterOperationAction(analyzer.AnalyzePropertyReference, OperationKind.PropertyReference);
        }

        private sealed class CompilationAnalyzer
        {
            private readonly HotPathDetector _hotPaths;

            internal CompilationAnalyzer(HotPathDetector hotPaths)
            {
                _hotPaths = hotPaths;
            }

            // `GetComponentsInChildren<T>()`, `Physics.RaycastAll`, `Object.FindObjectsOfType`,
            // `Animator.GetParameter`. Overloads that fill a caller-supplied List<T> or array return void
            // or int and are not matched.
            internal void AnalyzeInvocation(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                var method = invocation.TargetMethod;

                if (!IsEngineMember(method)) return;
                if (method.ReturnType is not IArrayTypeSymbol && !IsKnownAllocatingMethod(method)) return;

                Report(context, invocation.Syntax, method, method.ReturnType);
            }

            // `Camera.allCameras`, `Input.touches`, `mesh.vertices`, `renderer.materials`,
            // `renderer.sharedMaterials` (a copy of the array, though not of the materials), `name`, `tag`.
            // Writing the property (`mesh.vertices = buffer`) allocates nothing and is not matched.
            internal void AnalyzePropertyReference(OperationAnalysisContext context)
            {
                var reference = (IPropertyReferenceOperation)context.Operation;
                if (reference.Parent is IAssignmentOperation assignment && assignment.Target == reference) return;

                var property = reference.Property;
                if (!IsEngineMember(property)) return;
                if (property.Type is not IArrayTypeSymbol && !IsKnownAllocatingProperty(property)) return;

                Report(context, reference.Syntax, property, property.Type);
            }

            // The core `UnityEngine` namespace, and `UnityEngine.SceneManagement` and `UnityEngine.AI`,
            // where members are bindings to native code. Managed packages under `UnityEngine.UI` and
            // friends return their own fields without copying.
            private static bool IsEngineMember(ISymbol member)
            {
                var ns = member.ContainingType?.ContainingNamespace;
                if (ns == null) return false;

                if (ns.Name is "SceneManagement" or "AI") ns = ns.ContainingNamespace;

                return ns != null
                    && ns.Name.Equals("UnityEngine", System.StringComparison.Ordinal)
                    && ns.ContainingNamespace is { IsGlobalNamespace: true };
            }

            // Getters that build a new string or object on every read. `Application.dataPath` and the
            // other paths, and `Scene.name` and `path`, are marshalled from native strings each time;
            // `NavMeshAgent.path` copies the agent's path into a new NavMeshPath.
            private static bool IsKnownAllocatingProperty(IPropertySymbol property)
            {
                var typeName = property.ContainingType.Name;
                return property.Name switch
                {
                    "name" => typeName is "Object" or "Scene",
                    "tag" => typeName is "Component" or "GameObject",
                    "dataPath" or "persistentDataPath" or "streamingAssetsPath" or "temporaryCachePath" => typeName == "Application",
                    "path" => typeName is "Scene" or "NavMeshAgent",
                    _ => false,
                };
            }

            // `Animator.GetParameter(i)` reads `parameters`, a new array, and returns one element of it.
            // `Animator.GetLayerName` and `JsonUtility.ToJson` build a new string, and
            // `JsonUtility.FromJson` a new object (`FromJsonOverwrite` fills an existing one).
            private static bool IsKnownAllocatingMethod(IMethodSymbol method)
            {
                var typeName = method.ContainingType.Name;
                return method.Name switch
                {
                    "GetParameter" or "GetLayerName" => typeName == "Animator",
                    "ToJson" or "FromJson" => typeName == "JsonUtility",
                    _ => false,
                };
            }

            private void Report(OperationAnalysisContext context, SyntaxNode node, ISymbol member, ITypeSymbol returnType)
            {
                var semanticModel = context.Operation.SemanticModel;
                if (semanticModel == null) return;

                if (!_hotPaths.TryGetHotPathMethod(node, semanticModel, context.Options, context.CancellationToken, out var methodName))
                    return;

                var api = member.ContainingType.Name + "." + member.Name;
                var returned = returnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), api, returned, methodName));
            }
        }
    }
}
