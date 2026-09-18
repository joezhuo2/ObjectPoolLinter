using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using static ObjectPoolLinter.CodeFixSupport;

namespace ObjectPoolLinter
{
    // Syntax helpers shared by the OPL002 and OPL003 code fixes.
    internal static class CodeFixSupport
    {
        // The statement to insert new statements in front of. Moving work in front of the statement is safe
        // only when the moved expression is evaluated unconditionally, and nothing the statement evaluates
        // before it has side effects the moved expression could observe.
        internal static bool TryGetInsertionPoint(ExpressionSyntax expression, HotMethod hot, out StatementSyntax statement, out BlockSyntax block)
        {
            statement = null!;
            block = null!;

            if (expression.FirstAncestorOrSelf<StatementSyntax>() is not { Parent: BlockSyntax parent } found) return false;
            if (found is not (LocalDeclarationStatementSyntax or ExpressionStatementSyntax or ReturnStatementSyntax)) return false;
            if (found is LocalDeclarationStatementSyntax { UsingKeyword: var usingKeyword } && !usingKeyword.IsKind(SyntaxKind.None)) return false;
            if (found.Ancestors().TakeWhile(a => a != hot.Declaration).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) return false;

            for (SyntaxNode node = expression; node != found; node = node.Parent!)
            {
                var ancestor = node.Parent!;
                var conditional = ancestor switch
                {
                    AnonymousFunctionExpressionSyntax or ConditionalExpressionSyntax or ConditionalAccessExpressionSyntax
                        or SwitchExpressionSyntax or QueryExpressionSyntax => true,
                    BinaryExpressionSyntax binary => binary.Right == node &&
                        (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) || binary.IsKind(SyntaxKind.CoalesceExpression)),
                    AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression),
                    _ => false,
                };
                if (conditional) return false;

                foreach (var sibling in ancestor.ChildNodes())
                {
                    if (sibling.SpanStart >= node.SpanStart) break;
                    if (!IsPure(sibling)) return false;
                }
            }

            statement = found;
            block = parent;
            return true;
        }

        internal static bool IsPure(SyntaxNode node)
        {
            return node switch
            {
                ArgumentSyntax argument => IsSideEffectFree(argument.Expression),
                TypeSyntax => true,
                ExpressionSyntax expression => IsSideEffectFree(expression),
                _ => node.ChildNodes().All(IsPure),
            };
        }

        // Reads with no side effects. Property getters are assumed to have none.
        internal static bool IsSideEffectFree(ExpressionSyntax expression)
        {
            return expression switch
            {
                IdentifierNameSyntax or GenericNameSyntax or LiteralExpressionSyntax or ThisExpressionSyntax or BaseExpressionSyntax
                    or PredefinedTypeSyntax or TypeOfExpressionSyntax or DefaultExpressionSyntax => true,
                MemberAccessExpressionSyntax memberAccess => IsSideEffectFree(memberAccess.Expression),
                ParenthesizedExpressionSyntax parenthesized => IsSideEffectFree(parenthesized.Expression),
                CastExpressionSyntax cast => IsSideEffectFree(cast.Expression),
                ElementAccessExpressionSyntax elementAccess => IsSideEffectFree(elementAccess.Expression) &&
                    elementAccess.ArgumentList.Arguments.All(a => IsSideEffectFree(a.Expression)),
                BinaryExpressionSyntax binary => IsSideEffectFree(binary.Left) && IsSideEffectFree(binary.Right),
                PrefixUnaryExpressionSyntax unary => !unary.IsKind(SyntaxKind.PreIncrementExpression) &&
                    !unary.IsKind(SyntaxKind.PreDecrementExpression) && IsSideEffectFree(unary.Operand),
                _ => false,
            };
        }

        internal static BlockSyntax InsertBefore(BlockSyntax block, StatementSyntax statement, StatementSyntax replacement, IReadOnlyList<StatementSyntax> inserted)
        {
            var index = block.Statements.IndexOf(statement);
            var statements = block.Statements.RemoveAt(index);

            // The first new statement takes the statement's leading trivia, so a comment above the statement
            // stays above the code that now implements it.
            var first = inserted[0].WithLeadingTrivia(statement.GetLeadingTrivia());
            statements = statements.Insert(index, replacement.WithLeadingTrivia(SyntaxFactory.Whitespace(Indentation(statement))));
            statements = statements.InsertRange(index, new[] { first }.Concat(inserted.Skip(1)));

            return block.WithStatements(statements);
        }

        // The name the value is stored under or passed as: `text` in `var text = ...`, `onClick` in
        // `button.onClick += ...`, the parameter name for an argument.
        internal static string? NameHint(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            SyntaxNode node = expression;
            while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
                node = node.Parent;

            return node.Parent switch
            {
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => declarator.Identifier.ValueText,
                AssignmentExpressionSyntax { Left: IdentifierNameSyntax name } assignment when assignment.Right == node => name.Identifier.ValueText,
                AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax member } assignment when assignment.Right == node => member.Name.Identifier.ValueText,
                ArgumentSyntax argument => (model.GetOperation(argument, cancellationToken) as IArgumentOperation)?.Parameter?.Name,
                _ => null,
            };
        }

        internal static string FieldName(string? hint, string suffix, string fallback)
        {
            hint = hint?.TrimStart('_');
            if (hint == null || hint.Length == 0) return fallback;

            return "_" + char.ToLowerInvariant(hint[0]) + hint.Substring(1) + suffix;
        }

        internal static string FreeLocalName(string name, HashSet<string> used)
        {
            var candidate = name;
            for (var suffix = 2; used.Contains(candidate); suffix++)
                candidate = name + suffix;

            used.Add(candidate);
            return candidate;
        }

        // The simple name when it binds to the type, the qualified name when something else answers to it,
        // and the simple name plus a using directive when nothing does.
        internal static string TypeName(SemanticModel model, HotMethod hot, string namespaceName, string name, int arity, Rewrite rewrite)
        {
            var visible = model.LookupNamespacesAndTypes(hot.Declaration.SpanStart, name: name);
            if (visible.IsEmpty)
            {
                rewrite.Usings.Add(namespaceName);
                return name;
            }

            var matches = visible.OfType<INamedTypeSymbol>().Where(t => t.Arity == arity).ToList();
            if (visible.Length == 1 && matches.Count == 1 && matches[0].ContainingNamespace.ToDisplayString() == namespaceName)
                return name;

            return namespaceName + "." + name;
        }

        internal static string Display(ITypeSymbol type, SemanticModel model, HotMethod hot) =>
            type.ToMinimalDisplayString(model, hot.Declaration.SpanStart);

        // A type a field can be declared with: no anonymous or error types, and no type parameter of the
        // method the code is moving out of.
        internal static bool IsNameable(ITypeSymbol type)
        {
            return type switch
            {
                { TypeKind: TypeKind.Error } or { IsAnonymousType: true } => false,
                ITypeParameterSymbol parameter => parameter.TypeParameterKind == TypeParameterKind.Type,
                IArrayTypeSymbol array => IsNameable(array.ElementType),
                IPointerTypeSymbol => false,
                INamedTypeSymbol named => !named.IsImplicitlyDeclared &&
                    named.TypeArguments.All(IsNameable) &&
                    (named.ContainingType == null || IsNameable(named.ContainingType)),
                _ => true,
            };
        }

        internal static HashSet<string> IdentifierTexts(SyntaxNode scope) =>
            new(scope.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText));

        internal static string Indentation(SyntaxNode? node)
        {
            if (node == null) return "";

            var leading = node.GetLeadingTrivia();
            return leading.Count > 0 && leading[leading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)
                ? leading[leading.Count - 1].ToString()
                : "";
        }

        internal static string IndentUnit(ClassDeclarationSyntax declaration)
        {
            var classIndent = Indentation(declaration);
            var memberIndent = declaration.Members.Count > 0 ? Indentation(declaration.Members[0]) : "";

            return memberIndent.Length > classIndent.Length && memberIndent.StartsWith(classIndent, StringComparison.Ordinal)
                ? memberIndent.Substring(classIndent.Length)
                : "    ";
        }

        // Moves the second and later lines of multi-line code from one indentation level to another.
        internal static string Reindent(string text, string from, string to)
        {
            if (from == to || text.IndexOf('\n') < 0) return text;

            var lines = text.Split('\n');
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(from, StringComparison.Ordinal))
                    lines[i] = to + lines[i].Substring(from.Length);
            }

            return string.Join("\n", lines);
        }

        internal static string GetEndOfLine(SyntaxNode root)
        {
            var existing = root.DescendantTrivia(descendIntoTrivia: true)
                .FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

            return existing.IsKind(SyntaxKind.EndOfLineTrivia) ? existing.ToFullString() : "\r\n";
        }
    }

    // The method a diagnostic sits in, and the class declaring it.
    internal sealed class HotMethod
    {
        private HotMethod(MethodDeclarationSyntax declaration, ClassDeclarationSyntax @class, IMethodSymbol symbol, INamedTypeSymbol type)
        {
            Declaration = declaration;
            Class = @class;
            Symbol = symbol;
            Type = type;
        }

        internal MethodDeclarationSyntax Declaration { get; }
        internal ClassDeclarationSyntax Class { get; }
        internal IMethodSymbol Symbol { get; }
        internal INamedTypeSymbol Type { get; }

        internal static HotMethod? Find(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            if (node.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { Parent: ClassDeclarationSyntax @class, TypeParameterList: null } declaration)
                return null;

            if (model.GetDeclaredSymbol(declaration, cancellationToken) is not { } symbol ||
                model.GetDeclaredSymbol(@class, cancellationToken) is not { } type)
                return null;

            return new HotMethod(declaration, @class, symbol, type);
        }

        internal string FieldModifiers(bool readOnly) =>
            "private " + (Symbol.IsStatic ? "static " : "") + (readOnly ? "readonly " : "");

        internal MethodDeclarationSyntax? FindAwake() =>
            Class.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == "Awake" && m.ParameterList.Parameters.Count == 0);

        // Awake runs once, before any Update, on a MonoBehaviour. The fix appends to an Awake with a block
        // body in this declaration, or adds one when no Awake exists anywhere up to MonoBehaviour: a new
        // Awake would hide one declared in a base class or in another part of a partial class.
        internal bool CanAssignInAwake()
        {
            if (Symbol.IsStatic || Symbol.Name == "Awake") return false;
            if (!DerivesFromMonoBehaviour()) return false;

            var awake = FindAwake();
            if (awake != null) return awake.Body != null && !awake.Modifiers.Any(SyntaxKind.StaticKeyword);

            for (INamedTypeSymbol? type = Type; type != null && !IsMonoBehaviour(type); type = type.BaseType)
            {
                if (!type.GetMembers("Awake").IsEmpty) return false;
            }

            return true;
        }

        internal bool HasMember(string name)
        {
            for (INamedTypeSymbol? type = Type; type != null; type = type.BaseType)
            {
                if (!type.GetMembers(name).IsEmpty) return true;
            }

            return false;
        }

        internal string FreeMemberName(string name, ISet<string>? reserved = null)
        {
            var used = IdentifierTexts(Class);
            var candidate = name;
            for (var suffix = 2; used.Contains(candidate) || HasMember(candidate) || reserved?.Contains(candidate) == true; suffix++)
                candidate = name + suffix;

            return candidate;
        }

        private bool DerivesFromMonoBehaviour()
        {
            for (var type = Type.BaseType; type != null; type = type.BaseType)
            {
                if (IsMonoBehaviour(type)) return true;
            }

            return false;
        }

        private static bool IsMonoBehaviour(INamedTypeSymbol type) =>
            type.Name == "MonoBehaviour" && type.ContainingNamespace?.ToDisplayString() == "UnityEngine";
    }

    // The edits one fix makes: node replacements inside the class, fields and Awake statements to add,
    // and using directives the new code needs.
    internal sealed class Rewrite
    {
        private readonly HotMethod _hot;

        internal Rewrite(HotMethod hot, string title, string equivalenceKey)
        {
            _hot = hot;
            Title = title;
            EquivalenceKey = equivalenceKey;
        }

        internal string Title { get; }
        internal string EquivalenceKey { get; }
        internal Dictionary<SyntaxNode, SyntaxNode> Replacements { get; } = new();
        internal List<string> Fields { get; } = new();

        // Each takes the indentation of the line it lands on, for code spanning several lines.
        internal List<Func<string, string>> AwakeStatements { get; } = new();
        internal SortedSet<string> Usings { get; } = new(StringComparer.Ordinal);

        internal Document Apply(Document document, SyntaxNode root)
        {
            var declaration = _hot.Class;
            var eol = GetEndOfLine(root);
            var unit = IndentUnit(declaration);
            var memberIndent = declaration.Members.Count > 0 ? Indentation(declaration.Members[0]) : Indentation(declaration) + unit;

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>(Replacements);
            MemberDeclarationSyntax? newAwake = null;

            if (AwakeStatements.Count > 0)
            {
                var awake = _hot.FindAwake();
                if (awake?.Body is { } body)
                {
                    var indent = body.Statements.Count > 0 ? Indentation(body.Statements.Last()) : Indentation(awake) + unit;
                    var statements = AwakeStatements.Select(s => SyntaxFactory.ParseStatement(indent + s(indent) + eol));
                    replacements[awake] = awake.WithBody(body.WithStatements(body.Statements.AddRange(statements)));
                }
                else
                {
                    var indent = memberIndent + unit;
                    var text = memberIndent + "private void Awake()" + eol + memberIndent + "{" + eol +
                               string.Concat(AwakeStatements.Select(s => indent + s(indent) + eol)) +
                               memberIndent + "}" + eol;
                    newAwake = SyntaxFactory.ParseMemberDeclaration(text);
                }
            }

            var newClass = declaration.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            var members = newClass.Members;

            var fieldIndex = members.LastIndexOf(m => m is BaseFieldDeclarationSyntax) + 1;
            foreach (var field in Fields)
                members = members.Insert(fieldIndex++, SyntaxFactory.ParseMemberDeclaration(memberIndent + field + eol)!);
            members = EnsureBlankLineBefore(members, fieldIndex, eol);

            if (newAwake != null)
            {
                var awakeIndex = members.IndexOf(m => m is MethodDeclarationSyntax);
                if (awakeIndex < 0) awakeIndex = members.Count;

                members = members.Insert(awakeIndex, newAwake);
                if (awakeIndex > 0) members = EnsureBlankLineBefore(members, awakeIndex, eol);
                members = EnsureBlankLineBefore(members, awakeIndex + 1, eol);
            }

            var newRoot = root.ReplaceNode(declaration, newClass.WithMembers(members));
            if (newRoot is CompilationUnitSyntax compilationUnit)
                newRoot = AddUsings(compilationUnit, eol);

            return document.WithSyntaxRoot(newRoot);
        }

        private static SyntaxList<MemberDeclarationSyntax> EnsureBlankLineBefore(SyntaxList<MemberDeclarationSyntax> members, int index, string eol)
        {
            if (index >= members.Count) return members;

            var member = members[index];
            if (member.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))) return members;

            return members.Replace(member, member.WithLeadingTrivia(member.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine(eol))));
        }

        private CompilationUnitSyntax AddUsings(CompilationUnitSyntax compilationUnit, string eol)
        {
            foreach (var namespaceName in Usings)
            {
                var usings = compilationUnit.Usings;
                if (usings.Any(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name.ToString() == namespaceName))
                    continue;

                var directive = SyntaxFactory.ParseCompilationUnit("using " + namespaceName + ";" + eol).Usings[0];
                var index = usings.TakeWhile(u => string.CompareOrdinal(u.Name.ToString(), namespaceName) < 0).Count();

                if (usings.Count == 0)
                {
                    // The file's leading trivia (a header comment, blank lines) stays at the top.
                    var firstToken = compilationUnit.GetFirstToken();
                    directive = directive
                        .WithLeadingTrivia(firstToken.LeadingTrivia)
                        .WithTrailingTrivia(SyntaxFactory.EndOfLine(eol), SyntaxFactory.EndOfLine(eol));
                    compilationUnit = compilationUnit.ReplaceToken(firstToken, firstToken.WithLeadingTrivia());
                    compilationUnit = compilationUnit.WithUsings(compilationUnit.Usings.Add(directive));
                }
                else if (index == 0)
                {
                    var next = usings[0];
                    directive = directive.WithLeadingTrivia(next.GetLeadingTrivia());
                    usings = usings.Replace(next, next.WithLeadingTrivia());
                    compilationUnit = compilationUnit.WithUsings(usings.Insert(0, directive));
                }
                else
                {
                    compilationUnit = compilationUnit.WithUsings(usings.Insert(index, directive));
                }
            }

            return compilationUnit;
        }
    }
}
