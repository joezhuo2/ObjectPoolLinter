using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    // A code action that, once applied, counts itself in the local telemetry file. With telemetry off the
    // providers hand out their actions unwrapped, so nothing about them changes.
    //
    // The count is an extra operation after the edit. The IDE runs every operation when the user applies the
    // fix, but only the edit for a preview, so hovering over a fix counts nothing. A host that applies only
    // a fix's edit and skips other operations records nothing.
    internal sealed class TelemetryCodeAction : CodeAction
    {
        private readonly CodeAction _inner;
        private readonly string _section;

        private TelemetryCodeAction(CodeAction inner, string section)
        {
            _inner = inner;
            _section = section;
        }

        public override string Title => _inner.Title;

        public override string? EquivalenceKey => _inner.EquivalenceKey;

        internal static CodeAction Wrap(CodeAction action, bool telemetry) =>
            telemetry ? new TelemetryCodeAction(action, TelemetryLog.CodeFixesSection) : action;

        internal static async Task<CodeAction?> WrapFixAllAsync(CodeAction? action, FixAllContext fixAllContext)
        {
            if (action == null) return null;

            var document = fixAllContext.Document ?? fixAllContext.Project.Documents.FirstOrDefault();
            return document != null && await IsEnabledAsync(document, fixAllContext.CancellationToken).ConfigureAwait(false)
                ? new TelemetryCodeAction(action, TelemetryLog.FixAllSection)
                : action;
        }

        protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
        {
            var operations = await _inner.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            return operations.Concat(new[] { new RecordOperation(_section, EquivalenceKey ?? Title) });
        }

        protected override async Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
        {
            return await _inner.GetPreviewOperationsAsync(cancellationToken).ConfigureAwait(false);
        }

        // The same rule TelemetryAnalyzer applies: on when the file's options, or the global options, set
        // object_pool_linter.telemetry to true.
        internal static async Task<bool> IsEnabledAsync(Document document, CancellationToken cancellationToken)
        {
            var provider = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
            if (IsOn(provider.GlobalOptions)) return true;

            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            return tree != null && IsOn(provider.GetOptions(tree));
        }

        private static bool IsOn(AnalyzerConfigOptions options) =>
            options.TryGetValue(TelemetryAnalyzer.OptionName, out var value) &&
            value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        private sealed class RecordOperation : CodeActionOperation
        {
            private readonly string _section;
            private readonly string _key;

            internal RecordOperation(string section, string key)
            {
                _section = section;
                _key = key;
            }

            public override void Apply(Workspace workspace, CancellationToken cancellationToken) =>
                TelemetryLog.Increment(_section, _key);
        }
    }
}
