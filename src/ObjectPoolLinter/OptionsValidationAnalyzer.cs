using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    /// <summary>
    /// Reports OPL004: an <c>object_pool_linter.*</c> option in <c>.editorconfig</c> or
    /// <c>.globalconfig</c> that has no effect, because its name is not recognized or its value cannot be
    /// used. Without it, a misspelled option would be silently ignored.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class OptionsValidationAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The ID of the diagnostic this analyzer reports, <c>OPL004</c>.</summary>
        public const string DiagnosticId = "OPL004";

        private const string Category = "Configuration";
        private static readonly LocalizableString Title = "Invalid ObjectPoolLinter option";
        private static readonly LocalizableString MessageFormat = "ObjectPoolLinter option ignored: {0}";
        private static readonly LocalizableString Description = "An object_pool_linter.* entry in .editorconfig or .globalconfig has a name the linter does not read or a value it cannot use, so it has no effect.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL004.md";

        // Reported once per compilation, at the end, with no source location: the problem is in a
        // config file shared by many source files, not in any one of them.
        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
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

            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            var provider = context.Options.AnalyzerConfigOptionsProvider;
            var regexCache = new ConcurrentDictionary<string, Regex?>(System.StringComparer.Ordinal);

            // Files under the same .editorconfig sections usually share one options instance.
            var checkedOptions = new HashSet<AnalyzerConfigOptions>();
            var reported = new HashSet<string>(System.StringComparer.Ordinal);

            void Check(AnalyzerConfigOptions options)
            {
                if (!checkedOptions.Add(options)) return;

                foreach (var (key, suggestion) in LinterOptions.FindUnrecognizedOptions(options))
                {
                    var message = suggestion == null
                        ? $"'{key}' is not a recognized option."
                        : $"'{key}' is not a recognized option. Did you mean '{suggestion}'?";
                    Report(message);
                }

                foreach (var problem in LinterOptions.Parse(options, regexCache).Problems)
                    Report(problem);
            }

            void Report(string message)
            {
                if (reported.Add(message))
                    context.ReportDiagnostic(Diagnostic.Create(Rule, Location.None, message));
            }

            Check(provider.GlobalOptions);
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                Check(provider.GetOptions(tree));
            }
        }
    }
}
