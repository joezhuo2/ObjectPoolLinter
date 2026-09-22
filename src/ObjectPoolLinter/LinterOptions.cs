using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    // What OPL002 found, used to pick a per-kind severity from .editorconfig.
    internal enum AllocationKind
    {
        String,
        Delegate,
        Params,
        Linq,
        Boxing,
    }

    // The known-safe patterns ObjectPoolSuppressionAnalyzer suppresses a diagnostic for. Configured
    // with `object_pool_linter.suppressions`; every pattern is on unless the option narrows the set.
    [Flags]
    internal enum SuppressionKind
    {
        None = 0,
        FirstFrame = 1,
        EditorOnly = 2,
        StaticLatch = 4,
        CachedField = 8,
        All = FirstFrame | EditorOnly | StaticLatch | CachedField,
    }

    // User configuration from .editorconfig, read per syntax tree. Every name is matched ordinally, so
    // `tick` does not match `Tick`. A type name is its simple name (`Enemy`) or its namespace-qualified
    // name with nested types separated by dots (`Game.AI.Enemy.Brain`); generic type parameters are
    // left out. See docs/configuration.md.
    internal sealed class LinterOptions
    {
        internal const string Prefix = "object_pool_linter.";

        internal const string AdditionalHotMethodsOption = Prefix + "additional_hot_methods";
        internal const string ExcludedTypesOption = Prefix + "excluded_types";
        internal const string ExcludedTypesRegexOption = Prefix + "excluded_types_regex";

        internal const string StringSeverityOption = Prefix + "string_severity";
        internal const string DelegateSeverityOption = Prefix + "delegate_severity";
        internal const string ParamsSeverityOption = Prefix + "params_severity";
        internal const string LinqSeverityOption = Prefix + "linq_severity";
        internal const string BoxingSeverityOption = Prefix + "boxing_severity";

        internal const string SuppressionsOption = Prefix + "suppressions";

        internal static readonly ImmutableArray<string> KnownOptions = ImmutableArray.Create(
            AdditionalHotMethodsOption,
            ExcludedTypesOption,
            ExcludedTypesRegexOption,
            StringSeverityOption,
            DelegateSeverityOption,
            ParamsSeverityOption,
            LinqSeverityOption,
            BoxingSeverityOption,
            SuppressionsOption);

        private static readonly ImmutableArray<(AllocationKind Kind, string Option)> SeverityOptions = ImmutableArray.Create(
            (AllocationKind.String, StringSeverityOption),
            (AllocationKind.Delegate, DelegateSeverityOption),
            (AllocationKind.Params, ParamsSeverityOption),
            (AllocationKind.Linq, LinqSeverityOption),
            (AllocationKind.Boxing, BoxingSeverityOption));

        private static readonly ImmutableArray<(string Name, SuppressionKind Kind)> SuppressionNames = ImmutableArray.Create(
            ("first_frame", SuppressionKind.FirstFrame),
            ("editor_only", SuppressionKind.EditorOnly),
            ("static_latch", SuppressionKind.StaticLatch),
            ("cached_field", SuppressionKind.CachedField));

        // A pathological pattern must not hang the build or the IDE.
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        private static readonly LinterOptions Empty = new(
            ImmutableArray<HotMethodEntry>.Empty,
            ImmutableArray<string>.Empty,
            null,
            ImmutableDictionary<AllocationKind, ReportDiagnostic>.Empty,
            SuppressionKind.All,
            ImmutableArray<string>.Empty);

        private readonly ImmutableArray<HotMethodEntry> _additionalHotMethods;
        private readonly ImmutableArray<string> _excludedTypes;
        private readonly Regex? _excludedTypesRegex;
        private readonly ImmutableDictionary<AllocationKind, ReportDiagnostic> _severities;
        private readonly SuppressionKind _suppressions;

        private LinterOptions(
            ImmutableArray<HotMethodEntry> additionalHotMethods,
            ImmutableArray<string> excludedTypes,
            Regex? excludedTypesRegex,
            ImmutableDictionary<AllocationKind, ReportDiagnostic> severities,
            SuppressionKind suppressions,
            ImmutableArray<string> problems)
        {
            _additionalHotMethods = additionalHotMethods;
            _excludedTypes = excludedTypes;
            _excludedTypesRegex = excludedTypesRegex;
            _severities = severities;
            _suppressions = suppressions;
            Problems = problems;
        }

        // Values that could not be used, as messages for OPL004. Unrecognized option names are found
        // separately, by FindUnrecognizedOptions.
        internal ImmutableArray<string> Problems { get; }

        // `regexCache` holds the patterns already compiled for this compilation, so files sharing an
        // .editorconfig share one Regex. A null value means the pattern did not compile.
        internal static LinterOptions Parse(AnalyzerConfigOptions options, ConcurrentDictionary<string, Regex?>? regexCache = null)
        {
            var problems = ImmutableArray.CreateBuilder<string>();

            var additionalHotMethods = ImmutableArray.CreateBuilder<HotMethodEntry>();
            foreach (var entry in ParseList(options, AdditionalHotMethodsOption))
            {
                if (HotMethodEntry.TryParse(entry, out var parsed, out var error))
                    additionalHotMethods.Add(parsed!);
                else
                    problems.Add($"'{entry}' in '{AdditionalHotMethodsOption}' {error}.");
            }

            var excludedTypes = ParseList(options, ExcludedTypesOption);

            Regex? excludedTypesRegex = null;
            if (options.TryGetValue(ExcludedTypesRegexOption, out var pattern) && !string.IsNullOrWhiteSpace(pattern))
            {
                pattern = pattern.Trim();
                excludedTypesRegex = regexCache == null ? TryCompile(pattern) : regexCache.GetOrAdd(pattern, TryCompile);
                if (excludedTypesRegex == null)
                    problems.Add($"'{pattern}' in '{ExcludedTypesRegexOption}' is not a valid regular expression.");
            }

            var severities = ImmutableDictionary.CreateBuilder<AllocationKind, ReportDiagnostic>();
            foreach (var (kind, option) in SeverityOptions)
            {
                if (!options.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value)) continue;

                if (TryParseSeverity(value.Trim(), out var severity))
                    severities[kind] = severity;
                else
                    problems.Add($"'{value.Trim()}' in '{option}' is not a severity. Use none, silent, suggestion, warning, error or default.");
            }

            var suppressions = ParseSuppressions(options, problems);

            if (additionalHotMethods.Count == 0 && excludedTypes.IsEmpty && excludedTypesRegex == null &&
                severities.Count == 0 && suppressions == SuppressionKind.All && problems.Count == 0)
                return Empty;

            return new LinterOptions(
                additionalHotMethods.ToImmutable(),
                excludedTypes,
                excludedTypesRegex,
                severities.ToImmutable(),
                suppressions,
                problems.ToImmutable());
        }

        // Keys starting with `object_pool_linter.` that the linter does not read, each with the closest
        // known option when there is a plausible one. Empty when the host's Roslyn cannot list the keys
        // it holds (AnalyzerConfigOptions.Keys arrived after Roslyn 3.8).
        internal static IEnumerable<(string Key, string? Suggestion)> FindUnrecognizedOptions(AnalyzerConfigOptions options)
        {
            foreach (var key in GetKeys(options))
            {
                if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (KnownOptions.Contains(key)) continue;

                yield return (key, SuggestOption(key));
            }
        }

        internal bool IsExcludedType(INamedTypeSymbol type)
        {
            foreach (var entry in _excludedTypes)
            {
                if (TypeNameMatches(type, entry)) return true;
            }

            if (_excludedTypesRegex == null) return false;

            try
            {
                return _excludedTypesRegex.IsMatch(GetQualifiedName(type));
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        // Any signature, static or instance, on any type, unless the entry gives a parameter list: a
        // custom update loop is often a plain class driven by a manager.
        internal bool IsAdditionalHotMethod(IMethodSymbol method)
        {
            foreach (var entry in _additionalHotMethods)
            {
                if (entry.Matches(method)) return true;
            }

            return false;
        }

        // ReportDiagnostic.Default when the kind has no severity of its own.
        internal ReportDiagnostic GetSeverity(AllocationKind kind)
        {
            return _severities.TryGetValue(kind, out var severity) ? severity : ReportDiagnostic.Default;
        }

        // Whether the suppressor may suppress a diagnostic for this pattern.
        internal bool IsSuppressionEnabled(SuppressionKind kind)
        {
            return (_suppressions & kind) != 0;
        }

        // `all` (the default), `none`, or a list of pattern names. An unusable value leaves every
        // pattern on and is reported as OPL004 rather than silently narrowing the set.
        private static SuppressionKind ParseSuppressions(AnalyzerConfigOptions options, ImmutableArray<string>.Builder problems)
        {
            var entries = ParseList(options, SuppressionsOption);
            if (entries.IsEmpty) return SuppressionKind.All;

            var suppressions = SuppressionKind.None;
            var failed = false;

            foreach (var entry in entries)
            {
                var name = entry.ToLowerInvariant();

                if (name == "all")
                {
                    suppressions |= SuppressionKind.All;
                    continue;
                }

                if (name == "none") continue;

                var known = false;
                foreach (var (candidate, kind) in SuppressionNames)
                {
                    if (name != candidate) continue;

                    suppressions |= kind;
                    known = true;
                    break;
                }

                if (known) continue;

                failed = true;
                problems.Add($"'{entry}' in '{SuppressionsOption}' is not a suppression. Use all, none, " +
                             "first_frame, editor_only, static_latch or cached_field.");
            }

            return failed ? SuppressionKind.All : suppressions;
        }

        private static Regex? TryCompile(string pattern)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // The values `dotnet_diagnostic.<id>.severity` takes, plus `default` for the rule's own.
        private static bool TryParseSeverity(string value, out ReportDiagnostic severity)
        {
            switch (value.ToLowerInvariant())
            {
                case "none": severity = ReportDiagnostic.Suppress; return true;
                case "silent": severity = ReportDiagnostic.Hidden; return true;
                case "suggestion": severity = ReportDiagnostic.Info; return true;
                case "warning": severity = ReportDiagnostic.Warn; return true;
                case "error": severity = ReportDiagnostic.Error; return true;
                case "default": severity = ReportDiagnostic.Default; return true;
                default: severity = ReportDiagnostic.Default; return false;
            }
        }

        private static ImmutableArray<string> ParseList(AnalyzerConfigOptions options, string key)
        {
            if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                return ImmutableArray<string>.Empty;

            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var part in SplitTopLevel(value, ','))
            {
                var trimmed = StripGlobal(part.Trim());
                if (trimmed.Length > 0) builder.Add(trimmed);
            }

            return builder.ToImmutable();
        }

        // Splits on `separator` outside (), <> and [], so `Tick(float, int)` and `Dictionary<int, int>`
        // stay whole.
        private static List<string> SplitTopLevel(string value, char separator)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '(' or '<' or '[': depth++; break;
                    case ')' or '>' or ']': if (depth > 0) depth--; break;
                    default:
                        if (value[i] == separator && depth == 0)
                        {
                            parts.Add(value.Substring(start, i - start));
                            start = i + 1;
                        }
                        break;
                }
            }

            parts.Add(value.Substring(start));
            return parts;
        }

        private static string StripGlobal(string name)
        {
            return name.StartsWith("global::", StringComparison.Ordinal) ? name.Substring("global::".Length) : name;
        }

        private static bool TypeNameMatches(INamedTypeSymbol type, string name)
        {
            if (type.Name.Equals(name, StringComparison.Ordinal)) return true;
            if (name.IndexOf('.') < 0) return false;

            return GetQualifiedName(type).Equals(name, StringComparison.Ordinal);
        }

        private static string GetQualifiedName(INamedTypeSymbol type)
        {
            var name = type.Name;
            for (var containing = type.ContainingType; containing != null; containing = containing.ContainingType)
                name = containing.Name + "." + name;

            var ns = type.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }

        // AnalyzerConfigOptions.Keys exists from Roslyn 4.x; the analyzer builds against 3.8 so that
        // Unity 2021.3 can load it, and reaches the property by reflection when the host has it.
        private static readonly System.Reflection.PropertyInfo? KeysProperty = typeof(AnalyzerConfigOptions).GetProperty("Keys");

        private static IEnumerable<string> GetKeys(AnalyzerConfigOptions options)
        {
            if (KeysProperty == null) return Array.Empty<string>();

            try
            {
                return KeysProperty.GetValue(options) as IEnumerable<string> ?? Array.Empty<string>();
            }
            catch (System.Reflection.TargetInvocationException)
            {
                // The base implementation throws for hosts that do not override it.
                return Array.Empty<string>();
            }
        }

        private static string? SuggestOption(string key)
        {
            string? best = null;
            var bestDistance = int.MaxValue;

            foreach (var known in KnownOptions)
            {
                var distance = EditDistance(key.ToLowerInvariant(), known);
                if (distance < bestDistance)
                {
                    best = known;
                    bestDistance = distance;
                }
            }

            // A third of the name part is about the most a typo changes.
            return bestDistance <= Math.Max(2, (key.Length - Prefix.Length) / 3) ? best : null;
        }

        private static int EditDistance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        // One entry of additional_hot_methods: `Tick`, `EnemyBrain.Think`, `Tick(float)`,
        // `Game.AI.EnemyBrain.Think(ref Vector3, float)`. Without parentheses any parameter list matches.
        private sealed class HotMethodEntry
        {
            // A parameter type written by the user is compared, whitespace removed, against each of
            // these renderings, so `float`, `Single` and `System.Single` all match a float.
            private static readonly SymbolDisplayFormat[] ParameterFormats =
            {
                // float, Vector3, List<int>
                new(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes),
                // float, UnityEngine.Vector3, System.Collections.Generic.List<int>
                new(
                    globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes),
                // Single, List<Int32>
                new(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters),
                // System.Single, System.Collections.Generic.List<System.Int32>
                new(
                    globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters),
            };

            private readonly string? _typeName;
            private readonly string _methodName;

            // Default when the entry has no parameter list.
            private readonly ImmutableArray<(RefKind RefKind, string Type)> _parameters;

            private HotMethodEntry(string? typeName, string methodName, ImmutableArray<(RefKind, string)> parameters)
            {
                _typeName = typeName;
                _methodName = methodName;
                _parameters = parameters;
            }

            internal static bool TryParse(string entry, out HotMethodEntry? parsed, out string error)
            {
                parsed = null;
                error = string.Empty;

                var name = entry;
                var parameters = default(ImmutableArray<(RefKind, string)>);

                var open = entry.IndexOf('(');
                if (open >= 0)
                {
                    if (!entry.EndsWith(")", StringComparison.Ordinal))
                    {
                        error = "has a parameter list that does not end with ')'";
                        return false;
                    }

                    name = entry.Substring(0, open).Trim();
                    if (!TryParseParameters(entry.Substring(open + 1, entry.Length - open - 2), out parameters, out error))
                        return false;
                }
                else if (entry.IndexOf(')') >= 0)
                {
                    error = "has a ')' without a matching '('";
                    return false;
                }

                if (!IsDottedName(name))
                {
                    error = "is not a method name, Type.Method or Namespace.Type.Method";
                    return false;
                }

                var lastDot = name.LastIndexOf('.');
                parsed = lastDot < 0
                    ? new HotMethodEntry(null, name, parameters)
                    : new HotMethodEntry(name.Substring(0, lastDot), name.Substring(lastDot + 1), parameters);
                return true;
            }

            internal bool Matches(IMethodSymbol method)
            {
                if (!method.Name.Equals(_methodName, StringComparison.Ordinal)) return false;
                if (_typeName != null && !TypeNameMatches(method.ContainingType, _typeName)) return false;
                if (_parameters.IsDefault) return true;

                if (method.Parameters.Length != _parameters.Length) return false;

                for (var i = 0; i < _parameters.Length; i++)
                {
                    var parameter = method.Parameters[i];
                    if (parameter.RefKind != _parameters[i].RefKind) return false;
                    if (!ParameterTypeMatches(parameter.Type, _parameters[i].Type)) return false;
                }

                return true;
            }

            private static bool ParameterTypeMatches(ITypeSymbol type, string expected)
            {
                foreach (var format in ParameterFormats)
                {
                    if (RemoveWhitespace(type.ToDisplayString(format)).Equals(expected, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }

            // `ref`, `out` and `in` must match the parameter; `params` is accepted and ignored.
            private static bool TryParseParameters(string list, out ImmutableArray<(RefKind, string)> parameters, out string error)
            {
                parameters = ImmutableArray<(RefKind, string)>.Empty;
                error = string.Empty;
                if (string.IsNullOrWhiteSpace(list)) return true;

                var builder = ImmutableArray.CreateBuilder<(RefKind, string)>();
                foreach (var part in SplitTopLevel(list, ','))
                {
                    var text = part.Trim();
                    var refKind = RefKind.None;

                    var space = text.IndexOf(' ');
                    if (space > 0)
                    {
                        switch (text.Substring(0, space))
                        {
                            case "ref": refKind = RefKind.Ref; text = text.Substring(space + 1); break;
                            case "out": refKind = RefKind.Out; text = text.Substring(space + 1); break;
                            case "in": refKind = RefKind.In; text = text.Substring(space + 1); break;
                            case "params": text = text.Substring(space + 1); break;
                        }
                    }

                    var type = RemoveWhitespace(text).Replace("global::", string.Empty);
                    if (type.Length == 0)
                    {
                        error = "has an empty parameter type";
                        return false;
                    }

                    builder.Add((refKind, type));
                }

                parameters = builder.ToImmutable();
                return true;
            }

            private static bool IsDottedName(string name)
            {
                if (name.Length == 0 || name[0] == '.' || name[name.Length - 1] == '.') return false;

                for (var i = 0; i < name.Length; i++)
                {
                    var c = name[i];
                    if (c == '.')
                    {
                        if (name[i - 1] == '.') return false;
                    }
                    else if (!char.IsLetterOrDigit(c) && c != '_')
                    {
                        return false;
                    }
                }

                return true;
            }

            private static string RemoveWhitespace(string text)
            {
                var builder = new StringBuilder(text.Length);
                foreach (var c in text)
                {
                    if (!char.IsWhiteSpace(c)) builder.Append(c);
                }

                return builder.ToString();
            }
        }
    }
}
