using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using static ObjectPoolLinter.CodeFixSupport;

namespace ObjectPoolLinter
{
    /// <summary>
    /// Everything the writer below needs to emit a pool for one allocation: the pool satisfies the
    /// contract the "Replace with object pool Get()" fix looks for, so the same fix applies to every
    /// other allocation of the type once this one has been applied.
    /// </summary>
    internal sealed class PoolPlan
    {
        internal PoolPlan(
            string poolName,
            string declaration,
            string reference,
            string typeDisplay,
            string accessibility,
            string parameters,
            string arguments,
            string signature,
            string instanceName,
            IReadOnlyList<string> constraints,
            MemberDeclarationSyntax anchor)
        {
            PoolName = poolName;
            Declaration = declaration;
            Reference = reference;
            TypeDisplay = typeDisplay;
            Accessibility = accessibility;
            Parameters = parameters;
            Arguments = arguments;
            Signature = signature;
            InstanceName = instanceName;
            Constraints = constraints;
            Anchor = anchor;
        }

        // `EnemyPool`.
        internal string PoolName { get; }

        // How the pool is declared: `EnemyPool`, or `ListPool<T>` for a generic type.
        internal string Declaration { get; }

        // How the pool is named at the allocation site: `EnemyPool`, or `ListPool<int>`.
        internal string Reference { get; }

        // The pooled type as the generated file names it: `Enemy`, or `List<T>`.
        internal string TypeDisplay { get; }

        internal string Accessibility { get; }

        // The constructor's parameter list and the arguments forwarding it, both as source text.
        internal string Parameters { get; }

        internal string Arguments { get; }

        // `Enemy(int hp)`, for the TODO comment describing what a recycled instance has to look like.
        internal string Signature { get; }

        internal string InstanceName { get; }

        internal IReadOnlyList<string> Constraints { get; }

        // The type declaration the pool is written after: the outermost one in the file containing
        // the allocation, so the pool lands beside it rather than nested inside it.
        internal MemberDeclarationSyntax Anchor { get; }
    }

    /// <summary>
    /// Plans and writes the pool class the "Generate {TypeName}Pool" fix inserts. The output is
    /// ordinary, editable C# 7.3 - the same shape as the hand-written pool in the README, not the
    /// fully qualified source <see cref="PoolSourceWriter"/> generates from an <c>[ObjectPool]</c>
    /// attribute - because it lands in the developer's own file and is theirs to change afterwards.
    /// </summary>
    internal static class PoolClassWriter
    {
        private const string Stack = "System.Collections.Generic.Stack";
        private const string Field = "s_free";

        internal static PoolPlan? TryPlan(
            SemanticModel semanticModel,
            BaseObjectCreationExpressionSyntax objectCreation,
            CancellationToken cancellationToken)
        {
            if (objectCreation.Initializer != null) return null;

            var anchor = FindAnchor(objectCreation);
            if (anchor == null) return null;

            if (semanticModel.GetSymbolInfo(objectCreation, cancellationToken).Symbol is not IMethodSymbol constructor) return null;

            var type = constructor.ContainingType;
            if (type == null || type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic) return null;
            if (type.SpecialType == SpecialType.System_String || !IsNameable(type)) return null;

            // A UnityEngine.Object is created by Instantiate, not by the pool's `new`, so a pool of
            // them cannot be written mechanically.
            if (DerivesFromUnityObject(type)) return null;

            // A pool for a nested type of a generic type would have to repeat the outer type's
            // parameters, which the name `{TypeName}Pool` has nowhere to put.
            if (type.ContainingType is { IsGenericType: true }) return null;

            var accessibility = EffectiveAccessibility(type);
            if (accessibility is not (Accessibility.Public or Accessibility.Internal)) return null;

            var poolName = type.Name + "Pool";

            // Anything already answering to the name is left alone: the fix adds a type, it does not
            // reconcile with one that is already there.
            if (!semanticModel.LookupNamespacesAndTypes(objectCreation.SpanStart, name: poolName).IsEmpty) return null;

            if (!semanticModel.IsAccessible(anchor.Span.End, constructor)) return null;

            // Arguments are forwarded unchanged, so an omitted optional argument or an expanded
            // `params` list would not line up with the parameter list written here.
            var arguments = objectCreation.ArgumentList?.Arguments ?? default;
            var definition = constructor.OriginalDefinition;
            if (arguments.Count != definition.Parameters.Length) return null;
            if (definition.Parameters.Any(p => p.RefKind == RefKind.Out || !IsNameable(p.Type))) return null;

            var position = objectCreation.SpanStart;
            var typeParameters = type.OriginalDefinition.TypeParameters;
            var typeDisplay = type.OriginalDefinition.ToMinimalDisplayString(semanticModel, position);

            var constraints = new List<string>();
            foreach (var parameter in typeParameters)
            {
                if (parameter.ConstraintTypes.Any(t => !IsNameable(t))) return null;

                var clause = Constraint(parameter, semanticModel, position);
                if (clause != null) constraints.Add(clause);
            }

            var reference = poolName + (type.IsGenericType
                ? "<" + string.Join(", ", type.TypeArguments.Select(t => t.ToMinimalDisplayString(semanticModel, position))) + ">"
                : string.Empty);

            var declaration = poolName + (typeParameters.Length == 0
                ? string.Empty
                : "<" + string.Join(", ", typeParameters.Select(p => p.Name)) + ">");

            return new PoolPlan(
                poolName,
                declaration,
                reference,
                typeDisplay,
                accessibility == Accessibility.Public ? "public" : "internal",
                Parameters(definition, semanticModel, position),
                Arguments(definition),
                typeDisplay + "(" + Parameters(definition, semanticModel, position) + ")",
                InstanceName(definition),
                constraints,
                anchor);
        }

        internal static string Write(PoolPlan plan, string indent, string unit, string eol)
        {
            var body = indent + unit;
            var inner = body + unit;
            var stack = Stack + "<" + plan.TypeDisplay + ">";
            var instance = plan.InstanceName;

            var builder = new System.Text.StringBuilder();

            builder.Append(eol);
            builder.Append(indent).Append("/// <summary>").Append(eol);
            builder.Append(indent).Append("/// Object pool for <c>").Append(plan.TypeDisplay).Append("</c>. Not thread-safe, which is enough for").Append(eol);
            builder.Append(indent).Append("/// Unity's main-thread messages. Every instance taken out has to be handed back once.").Append(eol);
            builder.Append(indent).Append("/// </summary>").Append(eol);
            builder.Append(indent).Append(plan.Accessibility).Append(" static class ").Append(plan.Declaration).Append(eol);

            foreach (var constraint in plan.Constraints)
                builder.Append(body).Append(constraint).Append(eol);

            builder.Append(indent).Append('{').Append(eol);

            builder.Append(body).Append("private static readonly ").Append(stack).Append(' ').Append(Field)
                   .Append(" = new ").Append(stack).Append("();").Append(eol).Append(eol);

            builder.Append(body).Append("/// <summary>How many instances are waiting in the pool.</summary>").Append(eol);
            builder.Append(body).Append("public static int CountInactive").Append(eol);
            builder.Append(body).Append('{').Append(eol);
            builder.Append(inner).Append("get { return ").Append(Field).Append(".Count; }").Append(eol);
            builder.Append(body).Append('}').Append(eol).Append(eol);

            builder.Append(body).Append("/// <summary>Takes an instance out of the pool, or constructs one when the pool is empty.</summary>").Append(eol);
            builder.Append(body).Append("public static ").Append(plan.TypeDisplay).Append(" Get(").Append(plan.Parameters).Append(')').Append(eol);
            builder.Append(body).Append('{').Append(eol);
            builder.Append(inner).Append("if (").Append(Field).Append(".Count == 0) return new ").Append(plan.TypeDisplay)
                   .Append('(').Append(plan.Arguments).Append(");").Append(eol).Append(eol);
            builder.Append(inner).Append(plan.TypeDisplay).Append(' ').Append(instance).Append(" = ").Append(Field).Append(".Pop();").Append(eol);
            builder.Append(inner).Append("// TODO: put ").Append(instance).Append(" back into the state new ").Append(plan.Signature).Append(" would leave it in.").Append(eol);
            builder.Append(inner).Append("return ").Append(instance).Append(';').Append(eol);
            builder.Append(body).Append('}').Append(eol).Append(eol);

            builder.Append(body).Append("/// <summary>Hands an instance back to the pool. Return each instance exactly once.</summary>").Append(eol);
            builder.Append(body).Append("public static void Return(").Append(plan.TypeDisplay).Append(' ').Append(instance).Append(')').Append(eol);
            builder.Append(body).Append('{').Append(eol);
            builder.Append(inner).Append("if (").Append(instance).Append(" == null) throw new System.ArgumentNullException(\"").Append(instance).Append("\");").Append(eol).Append(eol);
            builder.Append(inner).Append("// TODO: release whatever ").Append(instance).Append(" holds on to before it waits in the pool.").Append(eol);
            builder.Append(inner).Append(Field).Append(".Push(").Append(instance).Append(");").Append(eol);
            builder.Append(body).Append('}').Append(eol).Append(eol);

            builder.Append(body).Append("/// <summary>Drops every instance waiting in the pool.</summary>").Append(eol);
            builder.Append(body).Append("public static void Clear()").Append(eol);
            builder.Append(body).Append('{').Append(eol);
            builder.Append(inner).Append(Field).Append(".Clear();").Append(eol);
            builder.Append(body).Append('}').Append(eol);

            builder.Append(indent).Append('}').Append(eol);

            return builder.ToString();
        }

        // The outermost type declaration in the file that contains the allocation. The pool is a
        // sibling of it, so it never lands inside a class it cannot be reached from.
        private static MemberDeclarationSyntax? FindAnchor(SyntaxNode node)
        {
            MemberDeclarationSyntax? anchor = null;

            for (var ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is BaseTypeDeclarationSyntax declaration) anchor = declaration;
            }

            // The outermost type declaration's parent is a namespace or the file itself, either of
            // which holds a member list the pool can be inserted into.
            return anchor;
        }

        private static bool DerivesFromUnityObject(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            {
                if (current.Name == "Object" && current.ContainingNamespace?.ToDisplayString() == "UnityEngine") return true;
            }

            return false;
        }

        // The narrowest accessibility along the nesting chain: a public type nested in an internal one
        // is internal in practice, and the pool's own accessibility has to match or the return type of
        // Get is less accessible than Get itself.
        private static Accessibility EffectiveAccessibility(INamedTypeSymbol type)
        {
            var accessibility = Accessibility.Public;

            for (INamedTypeSymbol? current = type; current != null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility < accessibility) accessibility = current.DeclaredAccessibility;
            }

            return accessibility;
        }

        private static string? Constraint(ITypeParameterSymbol parameter, SemanticModel semanticModel, int position)
        {
            var parts = new List<string>();

            // unmanaged and notnull also set the value-type / reference-type flags, so the most
            // specific one has to win here or the emitted clause is both wrong and redundant.
            if (parameter.HasReferenceTypeConstraint) parts.Add("class");
            else if (parameter.HasUnmanagedTypeConstraint) parts.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint) parts.Add("struct");
            else if (parameter.HasNotNullConstraint) parts.Add("notnull");

            foreach (var constraint in parameter.ConstraintTypes)
                parts.Add(constraint.ToMinimalDisplayString(semanticModel, position));

            if (parameter.HasConstructorConstraint) parts.Add("new()");

            return parts.Count == 0 ? null : "where " + parameter.Name + " : " + string.Join(", ", parts);
        }

        private static string Parameters(IMethodSymbol constructor, SemanticModel semanticModel, int position)
        {
            return string.Join(", ", constructor.Parameters.Select(parameter =>
                (parameter.IsParams ? "params " : string.Empty) +
                RefKindPrefix(parameter.RefKind) +
                parameter.Type.ToMinimalDisplayString(semanticModel, position) + " " +
                Identifier(parameter.Name)));
        }

        private static string Arguments(IMethodSymbol constructor)
        {
            // An `in` argument may be passed without the modifier; `ref` may not.
            return string.Join(", ", constructor.Parameters.Select(parameter =>
                (parameter.RefKind == RefKind.Ref ? "ref " : string.Empty) + Identifier(parameter.Name)));
        }

        private static string RefKindPrefix(RefKind refKind)
        {
            switch (refKind)
            {
                case RefKind.Ref: return "ref ";
                case RefKind.In: return "in ";
                default: return string.Empty;
            }
        }

        private static string InstanceName(IMethodSymbol constructor)
        {
            var name = "instance";
            while (constructor.Parameters.Any(p => p.Name == name)) name = "_" + name;

            return name;
        }

        private static string Identifier(string name)
        {
            return SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;
        }
    }
}
