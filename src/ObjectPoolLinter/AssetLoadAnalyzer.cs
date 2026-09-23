using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ObjectPoolLinter
{
    // OPL008: `Resources.Load` and Addressables `Load*` calls inside a hot path. Resources.Load looks
    // the asset up by path on every call, and an Addressables load allocates an operation handle and
    // its async state each time; either belongs in Awake or Start, with the result kept in a field.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AssetLoadAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL008";

        private const string Category = "Performance";
        private static readonly LocalizableString Title = "Asset loaded in hot path";
        private static readonly LocalizableString MessageFormat = "'{0}' loads an asset on every call inside the frequently-called method '{1}'. {2}.";
        private static readonly LocalizableString Description = "Resources.Load searches for the asset by path on every call, and Addressables.LoadAssetAsync and the other Addressables loads allocate an operation handle and async state each time. Inside frequently-invoked Unity methods (such as Update) that work and garbage repeat every frame; load once in Awake or Start and keep the result in a field.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL008.md";

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

        private const string ResourcesMetadataName = "UnityEngine.Resources";
        private const string AddressablesMetadataName = "UnityEngine.AddressableAssets.Addressables";
        private const string AssetReferenceMetadataName = "UnityEngine.AddressableAssets.AssetReference";

        private const string ResourcesAdvice = "Load it once in Awake or Start and keep it in a field";
        private const string AddressablesAdvice = "Load it once in Awake or Start and keep the handle or its result in a field";

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

            var resources = compilation.GetTypeByMetadataName(ResourcesMetadataName);
            var addressables = compilation.GetTypeByMetadataName(AddressablesMetadataName);
            var assetReference = compilation.GetTypeByMetadataName(AssetReferenceMetadataName);
            if (resources == null && addressables == null && assetReference == null) return;

            var analyzer = new CompilationAnalyzer(hotPaths, resources, addressables, assetReference);
            context.RegisterOperationAction(analyzer.AnalyzeInvocation, OperationKind.Invocation);
        }

        private sealed class CompilationAnalyzer
        {
            private readonly HotPathDetector _hotPaths;
            private readonly INamedTypeSymbol? _resources;
            private readonly INamedTypeSymbol? _addressables;
            private readonly INamedTypeSymbol? _assetReference;

            internal CompilationAnalyzer(HotPathDetector hotPaths, INamedTypeSymbol? resources, INamedTypeSymbol? addressables, INamedTypeSymbol? assetReference)
            {
                _hotPaths = hotPaths;
                _resources = resources;
                _addressables = addressables;
                _assetReference = assetReference;
            }

            internal void AnalyzeInvocation(OperationAnalysisContext context)
            {
                var invocation = (IInvocationOperation)context.Operation;
                var method = invocation.TargetMethod;

                var advice = GetAdvice(method);
                if (advice == null) return;

                var semanticModel = invocation.SemanticModel;
                if (semanticModel == null) return;

                if (!_hotPaths.TryGetHotPathMethod(invocation.Syntax, semanticModel, context.Options, context.CancellationToken, out var methodName))
                    return;

                var api = method.ContainingType.Name + "." + method.Name;
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), api, methodName, advice));
            }

            // `Resources.Load` and `Resources.LoadAsync`; `Resources.LoadAll` returns an array and is
            // OPL003's. Every `Addressables.Load*` (LoadAssetAsync, LoadAssetsAsync, LoadSceneAsync,
            // LoadResourceLocationsAsync, ...) and the `Load*` methods of an AssetReference.
            private string? GetAdvice(IMethodSymbol method)
            {
                var type = method.ContainingType;
                if (type == null) return null;

                if (_resources != null && SymbolEqualityComparer.Default.Equals(type, _resources))
                    return method.Name is "Load" or "LoadAsync" ? ResourcesAdvice : null;

                if (!method.Name.StartsWith("Load", System.StringComparison.Ordinal)) return null;

                if (_addressables != null && SymbolEqualityComparer.Default.Equals(type, _addressables)) return AddressablesAdvice;

                if (_assetReference == null) return null;

                for (var current = type.OriginalDefinition; current != null; current = current.BaseType?.OriginalDefinition)
                {
                    if (SymbolEqualityComparer.Default.Equals(current, _assetReference)) return AddressablesAdvice;
                }

                return null;
            }
        }
    }
}
