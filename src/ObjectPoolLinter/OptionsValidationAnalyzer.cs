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

            var compilationOptions = context.Compilation.Options;

            void Check(AnalyzerConfigOptions options, SyntaxTree? tree)
            {
                foreach (var message in LinterOptions.FindOverriddenKindSeverities(options, GetRuleWideSeverity(tree)))
                    Report(message);

                if (!checkedOptions.Add(options)) return;

                foreach (var message in LinterOptions.FindUnrecognizedKeys(options))
                    Report(message);

                foreach (var problem in LinterOptions.Parse(options, regexCache).Problems)
                    Report(problem);
            }

            // `dotnet_diagnostic.OPL002.severity` from .editorconfig or .globalconfig reaches the compiler
            // as tree options, not as analyzer options; a rule set or <NoWarn> as compilation options.
            ReportDiagnostic? GetRuleWideSeverity(SyntaxTree? tree)
            {
                var provider = compilationOptions.SyntaxTreeOptionsProvider;
                if (tree != null && provider != null &&
                    provider.TryGetDiagnosticValue(tree, HiddenAllocationAnalyzer.DiagnosticId, context.CancellationToken, out var severity))
                    return severity;

                if (provider != null && TryGetGlobalDiagnosticValue(provider, HiddenAllocationAnalyzer.DiagnosticId, context.CancellationToken, out severity))
                    return severity;

                return compilationOptions.SpecificDiagnosticOptions.TryGetValue(HiddenAllocationAnalyzer.DiagnosticId, out severity)
                    ? severity
                    : null;
            }

            void Report(string message)
            {
                if (reported.Add(message))
                    context.ReportDiagnostic(Diagnostic.Create(Rule, Location.None, message));
            }

            Check(provider.GlobalOptions, null);
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                Check(provider.GetOptions(tree), tree);
            }
        }

        // SyntaxTreeOptionsProvider.TryGetGlobalDiagnosticValue, which holds `dotnet_diagnostic.*` lines
        // from a .globalconfig, arrived after Roslyn 3.8; the analyzer builds against 3.8 so that Unity
        // 2021.3 can load it, and reaches the method by reflection when the host has it.
        private static readonly System.Reflection.MethodInfo? GlobalDiagnosticValueMethod =
            typeof(SyntaxTreeOptionsProvider).GetMethod(
                "TryGetGlobalDiagnosticValue",
                new[] { typeof(string), typeof(System.Threading.CancellationToken), typeof(ReportDiagnostic).MakeByRefType() });

        private static bool TryGetGlobalDiagnosticValue(
            SyntaxTreeOptionsProvider provider, string id, System.Threading.CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = ReportDiagnostic.Default;
            if (GlobalDiagnosticValueMethod == null) return false;

            var arguments = new object?[] { id, cancellationToken, null };
            try
            {
                if (GlobalDiagnosticValueMethod.Invoke(provider, arguments) is not true) return false;
            }
            catch (System.Reflection.TargetInvocationException)
            {
                return false;
            }

            severity = (ReportDiagnostic)arguments[2]!;
            return true;
        }
    }
}
