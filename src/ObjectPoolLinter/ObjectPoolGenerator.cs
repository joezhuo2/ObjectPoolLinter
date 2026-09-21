using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ObjectPoolLinter
{
    /// <summary>
    /// Emits the <c>[ObjectPool]</c> attribute, and for every class carrying it a
    /// <c>{TypeName}Pool</c> class shaped like the pool OPL001's "Replace with object pool
    /// <c>Get()</c>" fix looks for: a static type named after the pooled type, with one static
    /// <c>Get</c> overload per constructor.
    /// </summary>
    [Generator]
    public sealed class ObjectPoolGenerator : ISourceGenerator
    {
        public const string DiagnosticId = "OPL005";

        internal const string AttributeNamespace = "ObjectPoolLinter";
        internal const string AttributeSimpleName = "ObjectPool";
        internal const string AttributeTypeName = AttributeSimpleName + "Attribute";
        internal const string AttributeMetadataName = AttributeNamespace + "." + AttributeTypeName;
        public const string AttributeHintName = AttributeTypeName + ".g.cs";

        internal const string PoolNameProperty = "PoolName";
        internal const string InitialCapacityProperty = "InitialCapacity";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL005.md";

        private const string UnityObjectMetadataName = "UnityEngine.Object";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            "No object pool can be generated for this type",
            "'{0}' is marked [ObjectPool] but no pool was generated: {1}",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The generator writes a pool that names the marked type and calls its constructors, so it only runs for types it can name and instantiate. Where it cannot, the attribute would silently do nothing at all.",
            helpLinkUri: HelpLinkUri
        );

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // Roslyn 3.8 has no post-initialization step, so the attribute is added from here. The
            // compilation handed to a generator never contains that generator's own output, which is
            // why candidates below are matched on the attribute's written name rather than its symbol.
            context.AddSource(AttributeHintName, AttributeSource);

            if (context.SyntaxReceiver is not SyntaxReceiver receiver || receiver.Candidates.Count == 0) return;

            var requests = Collect(context, receiver.Candidates);
            if (requests.Count == 0) return;

            foreach (var request in WithoutCollisions(context, requests))
                context.AddSource(request.HintName, PoolSourceWriter.Write(request));
        }

        private static List<PoolRequest> Collect(GeneratorExecutionContext context, List<ClassDeclarationSyntax> candidates)
        {
            var requests = new List<PoolRequest>();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var unityObject = context.Compilation.GetTypeByMetadataName(UnityObjectMetadataName);

            foreach (var candidate in candidates)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var model = context.Compilation.GetSemanticModel(candidate.SyntaxTree);
                var attribute = FindObjectPoolAttribute(candidate, model, context.CancellationToken);
                if (attribute == null) continue;

                if (model.GetDeclaredSymbol(candidate, context.CancellationToken) is not INamedTypeSymbol type) continue;

                // A partial class can carry the attribute on more than one of its declarations.
                if (!seen.Add(type)) continue;

                if (TryCreateRequest(context, type, attribute, model, candidate.Identifier.GetLocation(), unityObject, out var request))
                    requests.Add(request!);
            }

            return requests;
        }

        private static AttributeSyntax? FindObjectPoolAttribute(
            ClassDeclarationSyntax declaration,
            SemanticModel model,
            System.Threading.CancellationToken cancellationToken)
        {
            foreach (var list in declaration.AttributeLists)
            {
                foreach (var attribute in list.Attributes)
                {
                    var name = SimpleName(attribute.Name);
                    if (name != AttributeSimpleName && name != AttributeTypeName) continue;

                    // The generated attribute is absent from this compilation, so an unresolved name
                    // is the expected case. A name that does resolve belongs to someone else, unless
                    // it happens to be ours because a consumer declared the attribute by hand.
                    var resolved = model.GetSymbolInfo(attribute, cancellationToken).Symbol?.ContainingType;
                    if (resolved != null && resolved.ToDisplayString() != AttributeMetadataName) continue;

                    return attribute;
                }
            }

            return null;
        }

        private static bool TryCreateRequest(
            GeneratorExecutionContext context,
            INamedTypeSymbol type,
            AttributeSyntax attribute,
            SemanticModel model,
            Location location,
            INamedTypeSymbol? unityObject,
            out PoolRequest? request)
        {
            request = null;

            if (type.IsStatic)
                return Reject(context, type, location, "it is a static class");

            if (type.IsAbstract)
                return Reject(context, type, location, "it is abstract, so the pool has nothing to construct");

            var accessibility = EffectiveAccessibility(type);
            if (accessibility < Accessibility.Internal)
                return Reject(context, type, location, "it is not visible to a type declared in its namespace");

            if (unityObject != null && InheritsFrom(type, unityObject))
                return Reject(context, type, location, "a UnityEngine.Object is created with Instantiate rather than with new, which a generated pool cannot do");

            ReadOptions(attribute, model, context.CancellationToken, out var poolNameOverride, out var initialCapacity);

            if (poolNameOverride != null && !SyntaxFacts.IsValidIdentifier(poolNameOverride))
                return Reject(context, type, location, PoolNameProperty + " '" + poolNameOverride + "' is not a valid C# identifier");

            if (initialCapacity < 0)
                return Reject(context, type, location, InitialCapacityProperty + " is negative");

            var constructors = UsableConstructors(type);
            if (constructors.Count == 0)
                return Reject(context, type, location, "it has no constructor the pool can call - every one is private, protected, or takes an 'out' parameter");

            var poolName = poolNameOverride ?? type.Name + "Pool";
            var typeParameters = AllTypeParameters(type);
            var modifier = accessibility == Accessibility.Public ? "public" : "internal";

            var existing = FindExistingPoolType(context.Compilation, type.ContainingNamespace, poolName, typeParameters.Count);
            if (existing != null)
            {
                // A hand-written `static partial class EnemyPool` is how a consumer supplies the
                // Reinitialize and OnReturn hooks, so that one is expected. Anything else becomes a
                // duplicate definition the moment the generated file joins the compilation.
                if (!existing.IsStatic || !IsDeclaredPartial(existing))
                    return Reject(context, type, location, "a type named '" + poolName + "' already exists here and is not a static partial class");

                modifier = SyntaxFacts.GetText(existing.DeclaredAccessibility);
            }

            request = new PoolRequest(type, poolName, modifier, initialCapacity, constructors, typeParameters, location);
            return true;
        }

        private static bool Reject(GeneratorExecutionContext context, INamedTypeSymbol type, Location location, string reason)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name, reason));
            return false;
        }

        private static void ReadOptions(
            AttributeSyntax attribute,
            SemanticModel model,
            System.Threading.CancellationToken cancellationToken,
            out string? poolName,
            out int initialCapacity)
        {
            poolName = null;
            initialCapacity = 0;

            if (attribute.ArgumentList == null) return;

            // The attribute type is unresolved here, so the values come off the literals instead of
            // an AttributeData. That is enough: both properties only accept compile-time constants.
            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                var name = argument.NameEquals?.Name.Identifier.ValueText;
                if (name == null) continue;

                var constant = model.GetConstantValue(argument.Expression, cancellationToken);
                if (!constant.HasValue) continue;

                if (name == PoolNameProperty && constant.Value is string text) poolName = text;
                else if (name == InitialCapacityProperty && constant.Value is int capacity) initialCapacity = capacity;
            }
        }

        private static IEnumerable<PoolRequest> WithoutCollisions(GeneratorExecutionContext context, List<PoolRequest> requests)
        {
            var byName = new Dictionary<string, List<PoolRequest>>();
            foreach (var request in requests)
            {
                if (!byName.TryGetValue(request.HintName, out var group))
                    byName[request.HintName] = group = new List<PoolRequest>();

                group.Add(request);
            }

            foreach (var group in byName.Values)
            {
                if (group.Count == 1)
                {
                    yield return group[0];
                    continue;
                }

                // Two types in one namespace asking for the same pool name, which happens when
                // identically named types nest under different outer types, or when PoolName repeats.
                foreach (var request in group)
                    Reject(context, request.Type, request.Location, "another type in the same namespace also generates '" + request.PoolName + "'");
            }
        }

        private static INamedTypeSymbol? FindExistingPoolType(Compilation compilation, INamespaceSymbol containingNamespace, string poolName, int arity)
        {
            var metadataName = containingNamespace.IsGlobalNamespace
                ? poolName
                : containingNamespace.ToDisplayString() + "." + poolName;

            if (arity > 0) metadataName += "`" + arity.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return compilation.GetTypeByMetadataName(metadataName);
        }

        private static bool IsDeclaredPartial(INamedTypeSymbol type)
        {
            if (type.DeclaringSyntaxReferences.Length == 0) return false;

            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not ClassDeclarationSyntax declaration) return false;
                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword)) return false;
            }

            return true;
        }

        private static List<IMethodSymbol> UsableConstructors(INamedTypeSymbol type)
        {
            var usable = new List<IMethodSymbol>();

            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.DeclaredAccessibility != Accessibility.Public &&
                    constructor.DeclaredAccessibility != Accessibility.Internal &&
                    constructor.DeclaredAccessibility != Accessibility.ProtectedOrInternal)
                    continue;

                if (constructor.IsVararg) continue;

                // The call to an unimplemented partial method is erased, which would leave an 'out'
                // parameter unassigned on the branch that reuses a pooled instance.
                var forwardable = true;
                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.RefKind == RefKind.Out)
                    {
                        forwardable = false;
                        break;
                    }
                }

                if (forwardable) usable.Add(constructor);
            }

            return usable;
        }

        private static List<ITypeParameterSymbol> AllTypeParameters(INamedTypeSymbol type)
        {
            var nesting = new List<INamedTypeSymbol>();
            for (INamedTypeSymbol? current = type; current != null; current = current.ContainingType)
                nesting.Add(current);

            nesting.Reverse();

            var parameters = new List<ITypeParameterSymbol>();
            foreach (var level in nesting)
                parameters.AddRange(level.TypeParameters);

            return parameters;
        }

        private static Accessibility EffectiveAccessibility(INamedTypeSymbol type)
        {
            var effective = Accessibility.Public;

            for (INamedTypeSymbol? current = type; current != null; current = current.ContainingType)
            {
                // protected internal is reachable from the namespace; the rest of the Accessibility
                // enum already runs from least to most visible, so the minimum is the effective one.
                var declared = current.DeclaredAccessibility;
                if (declared == Accessibility.ProtectedOrInternal) declared = Accessibility.Internal;
                if (declared < effective) effective = declared;
            }

            return effective;
        }

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            }

            return false;
        }

        private static string? SimpleName(NameSyntax name)
        {
            return name switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                _ => null
            };
        }

        internal const string AttributeSource = @"// <auto-generated/>
#pragma warning disable

namespace " + AttributeNamespace + @"
{
    /// <summary>
    /// Generates a pool for this type: a static class named <c>{TypeName}Pool</c> carrying one static
    /// <c>Get</c> overload per constructor, plus <c>Return</c>, <c>Clear</c> and <c>CountInactive</c>.
    /// Implement the generated <c>Reinitialize</c> and <c>OnReturn</c> partial methods in your own
    /// <c>static partial class {TypeName}Pool</c> to reset instances that come back out of the pool.
    /// </summary>
    [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class " + AttributeTypeName + @" : global::System.Attribute
    {
        /// <summary>Name of the generated pool class. Defaults to <c>{TypeName}Pool</c>, which is the
        /// only name OPL001's code fix looks for.</summary>
        public string " + PoolNameProperty + @" { get; set; }

        /// <summary>Capacity the pool's backing stack starts with. Defaults to 0.</summary>
        public int " + InitialCapacityProperty + @" { get; set; }
    }
}
";

        private sealed class SyntaxReceiver : ISyntaxReceiver
        {
            internal List<ClassDeclarationSyntax> Candidates { get; } = new();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is ClassDeclarationSyntax declaration && CarriesAttributeName(declaration))
                    Candidates.Add(declaration);
            }

            private static bool CarriesAttributeName(ClassDeclarationSyntax declaration)
            {
                foreach (var list in declaration.AttributeLists)
                {
                    foreach (var attribute in list.Attributes)
                    {
                        var name = SimpleName(attribute.Name);
                        if (name == AttributeSimpleName || name == AttributeTypeName) return true;
                    }
                }

                return false;
            }
        }
    }
}
