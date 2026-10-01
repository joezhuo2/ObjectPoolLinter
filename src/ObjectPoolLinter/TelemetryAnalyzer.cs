using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    /// <summary>
    /// Reports OPL010 when <c>object_pool_linter.telemetry = true</c>: once per compilation, how many times
    /// each ObjectPoolLinter rule fired in it. The summary is an ordinary diagnostic, so it stays on the
    /// machine, in the build output, the IDE's error list and any SARIF log the build writes. Nothing is
    /// sent anywhere, and it holds rule IDs and counts only: no file names, paths, symbols or code.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class TelemetryAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The ID of the diagnostic this analyzer reports, <c>OPL010</c>.</summary>
        public const string DiagnosticId = "OPL010";

        /// <summary>
        /// The <c>.editorconfig</c> option that turns telemetry on, <c>object_pool_linter.telemetry</c>. It is
        /// off unless set to <c>true</c>; the code fixes read it too, to count the fixes applied.
        /// </summary>
        public const string OptionName = LinterOptions.TelemetryOption;

        private const string Category = "Telemetry";
        private static readonly LocalizableString Title = "ObjectPoolLinter telemetry summary";
        private static readonly LocalizableString MessageFormat = "ObjectPoolLinter telemetry: {0} ({1} source files)";
        private static readonly LocalizableString Description = "With object_pool_linter.telemetry = true, the number of times each ObjectPoolLinter rule fired in the compilation. Reported once, with no source location, and only when the option is on.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL010.md";

        // The property holding the number of source files, next to one property per rule ID.
        internal const string SourceFilesProperty = "SourceFiles";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri,
            // WellKnownDiagnosticTags.CompilationEnd, which Roslyn 3.8 does not define.
            customTags: "CompilationEnd"
        );

        /// <inheritdoc/>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        /// <inheritdoc/>
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // RS1013 would have the start/end pair become one compilation action, but that runs at the end,
            // after the rules have reported: the counts must exist before any rule records into them.
#pragma warning disable RS1013
            context.RegisterCompilationStartAction(start =>
            {
                if (!LinterOptions.IsTelemetryEnabled(start.Options.AnalyzerConfigOptionsProvider, start.Compilation)) return;

                var counts = TelemetryCounts.Start(start.Compilation);
                start.RegisterCompilationEndAction(end =>
                    end.ReportDiagnostic(counts.CreateSummary(Rule, end.Compilation.SyntaxTrees.Count())));
            });
#pragma warning restore RS1013
        }
    }

    // How many times each rule fired in one compilation. The rules record into it as they report; the
    // counts exist only for a compilation TelemetryAnalyzer started counting for, so with telemetry off a
    // record is one failed table lookup. The driver runs every analyzer's compilation-start actions before
    // any syntax, symbol or operation action, and compilation-end actions after all of them, so the
    // summary sees every count.
    internal sealed class TelemetryCounts
    {
        private static readonly ConditionalWeakTable<Compilation, TelemetryCounts> ByCompilation = new();

        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        // A compilation analyzed again, as the IDE can, starts from zero.
        internal static TelemetryCounts Start(Compilation compilation)
        {
            lock (ByCompilation)
            {
                ByCompilation.Remove(compilation);
                return ByCompilation.GetValue(compilation, _ => new TelemetryCounts());
            }
        }

        // Counts the diagnostic as reported, before #pragma, [SuppressMessage] or the suppressor remove it.
        internal static void Record(Compilation compilation, Diagnostic diagnostic)
        {
            if (ByCompilation.TryGetValue(compilation, out var counts))
                counts._counts.AddOrUpdate(diagnostic.Id, 1, (_, count) => count + 1);
        }

        // Most frequent rule first.
        internal Diagnostic CreateSummary(DiagnosticDescriptor rule, int sourceFiles)
        {
            var ordered = _counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();

            var properties = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
            foreach (var pair in ordered)
                properties[pair.Key] = pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            properties[TelemetryAnalyzer.SourceFilesProperty] = sourceFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var summary = ordered.Count == 0
                ? "no diagnostics"
                : string.Join(", ", ordered.Select(pair => pair.Key + ": " + pair.Value));

            return Diagnostic.Create(rule, Location.None, properties.ToImmutable(), summary, sourceFiles);
        }
    }
}
