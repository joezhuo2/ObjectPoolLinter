using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace ObjectPoolLinter
{
    // Another fix-all provider, here Roslyn's batch fixer for OPL001, with its Fix All action counted in the
    // local telemetry file when telemetry is on.
    internal sealed class TelemetryFixAllProvider : FixAllProvider
    {
        internal TelemetryFixAllProvider(FixAllProvider inner)
        {
            Inner = inner;
        }

        internal FixAllProvider Inner { get; }

        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes() => Inner.GetSupportedFixAllScopes();

        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            var action = await Inner.GetFixAsync(fixAllContext).ConfigureAwait(false);
            return await TelemetryCodeAction.WrapFixAllAsync(action, fixAllContext).ConfigureAwait(false);
        }
    }
}
