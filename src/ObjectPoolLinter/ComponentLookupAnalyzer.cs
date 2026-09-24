using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ObjectPoolLinter
{
    // OPL009: `GetComponent<T>()` and the scene searches (`GameObject.Find`, `FindObjectOfType`, ...)
    // inside a hot path. They allocate nothing, but repeat a component lookup or a scene walk every
    // frame to get back the same object; that belongs in Awake or Start, with the result kept in a field.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ComponentLookupAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL009";

        private const string Category = "Performance";
        private static readonly LocalizableString Title = "Component or object lookup in hot path";
        private static readonly LocalizableString MessageFormat = "'{0}' {1} on every call inside the frequently-called method '{2}'. {3}.";
        private static readonly LocalizableString Description = "GetComponent, TryGetComponent, GetComponentInChildren and GetComponentInParent search the GameObject's components on every call, and GameObject.Find, FindWithTag and FindObjectOfType search the whole scene. They allocate no garbage, but inside frequently-invoked Unity methods (such as Update) the same lookup repeats every frame; do it once in Awake or Start and keep the result in a field.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL009.md";

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

        private const string ObjectMetadataName = "UnityEngine.Object";
        private const string ComponentMetadataName = "UnityEngine.Component";
        private const string GameObjectMetadataName = "UnityEngine.GameObject";

        private const string ComponentLookup = "looks up a component";
        private const string SceneSearch = "searches the scene";

        private const string ComponentAdvice = "Get it once in Awake or Start and keep it in a field";
        private const string SceneAdvice = "Find it once in Awake or Start and keep it in a field";

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

            var hotPaths = HotPathDetector.Create(compilation);
            if (hotPaths == null) return;

            var analyzer = new CompilationAnalyzer(
                hotPaths,
                compilation.GetTypeByMetadataName(ObjectMetadataName),
                compilation.GetTypeByMetadataName(ComponentMetadataName),
                compilation.GetTypeByMetadataName(GameObjectMetadataName));

            context.RegisterOperationAction(analyzer.AnalyzeInvocation, OperationKind.Invocation);
        }

        private sealed class CompilationAnalyzer
        {
            private readonly HotPathDetector _hotPaths;
            private readonly INamedTypeSymbol? _object;
            private readonly INamedTypeSymbol? _component;
            private readonly INamedTypeSymbol? _gameObject;

            internal CompilationAnalyzer(HotPathDetector hotPaths, INamedTypeSymbol? unityObject, INamedTypeSymbol? component, INamedTypeSymbol? gameObject)
            {
                _hotPaths = hotPaths;
                _object = unityObject;
                _component = component;
                _gameObject = gameObject;
            }

            internal void AnalyzeInvocation(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                var method = invocation.TargetMethod;

                // The array-returning `GetComponents*`, `FindObjectsOfType` and `FindGameObjectsWithTag`
                // allocate and are OPL003's.
                if (method.ReturnType is IArrayTypeSymbol) return;

                var (kind, advice) = Classify(invocation);
                if (kind == null) return;

                var semanticModel = invocation.SemanticModel;
                if (semanticModel == null) return;

                if (!_hotPaths.TryGetHotPathMethod(invocation.Syntax, semanticModel, context.Options, context.CancellationToken, out var methodName))
                    return;

                var api = method.ContainingType.Name + "." + method.Name;
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), api, kind, methodName, advice));
            }

            private (string? Kind, string? Advice) Classify(IInvocationOperation invocation)
            {
                var method = invocation.TargetMethod;
                var type = method.ContainingType?.OriginalDefinition;
                if (type == null) return (null, null);

                if (method.IsStatic)
                {
                    var isSceneSearch = method.Name switch
                    {
                        "Find" or "FindWithTag" or "FindGameObjectWithTag" => Is(type, _gameObject),
                        "FindObjectOfType" or "FindFirstObjectByType" or "FindAnyObjectByType" => Is(type, _object),
                        _ => false,
                    };
                    return isSceneSearch ? (SceneSearch, SceneAdvice) : (null, null);
                }

                if (method.Name is not ("GetComponent" or "TryGetComponent" or "GetComponentInChildren" or "GetComponentInParent"))
                    return (null, null);
                if (!Is(type, _component) && !Is(type, _gameObject)) return (null, null);

                // `other.GetComponent<Health>()` in OnTriggerStay, or on a raycast hit, looks up a
                // different object each frame and cannot be cached. Only a lookup on this object, or on
                // one reached through its fields and properties, returns the same component every time.
                return IsStableReceiver(invocation.Instance) ? (ComponentLookup, ComponentAdvice) : (null, null);
            }

            private static bool Is(INamedTypeSymbol type, INamedTypeSymbol? expected) =>
                expected != null && SymbolEqualityComparer.Default.Equals(type, expected);

            // `this`, `gameObject`, `transform.parent`, `_target`, `Player.Instance`: a chain of field and
            // property reads rooted at `this` or at a static member. Locals, parameters, method results
            // and indexers are not followed, nor is a member of a struct such as `_hit.collider`, which
            // is data refreshed each frame rather than a reference to the same object.
            private static bool IsStableReceiver(IOperation? receiver)
            {
                while (true)
                {
                    if (receiver is IMemberReferenceOperation { Instance.Type.IsValueType: true }) return false;

                    switch (receiver)
                    {
                        case IInstanceReferenceOperation:
                            return true;
                        case IConversionOperation conversion when conversion.IsImplicit:
                            receiver = conversion.Operand;
                            break;
                        case IConditionalAccessInstanceOperation:
                            receiver = GetConditionalAccessReceiver(receiver);
                            break;
                        case IFieldReferenceOperation field:
                            if (field.Instance == null) return true;
                            receiver = field.Instance;
                            break;
                        case IPropertyReferenceOperation property when property.Arguments.IsEmpty:
                            if (property.Instance == null) return true;
                            receiver = property.Instance;
                            break;
                        default:
                            return false;
                    }
                }
            }

            // `_target?.GetComponent<T>()`: the receiver is the expression left of the `?.`.
            private static IOperation? GetConditionalAccessReceiver(IOperation instance)
            {
                for (var current = instance.Parent; current != null; current = current.Parent)
                {
                    if (current is IConditionalAccessOperation access) return access.Operation;
                }

                return null;
            }
        }
    }
}
