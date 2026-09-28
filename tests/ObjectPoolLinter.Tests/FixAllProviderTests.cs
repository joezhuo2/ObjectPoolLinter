using System;
using System.Linq;
using Microsoft.CodeAnalysis.CodeFixes;
using Xunit;

namespace ObjectPoolLinter.Tests
{
    // T48: the fix-all provider each code fix hands to the IDE. OPL001's fixes are independent of each
    // other and batch safely; ObjectPoolCodeFixProviderTests runs a batch fix-all over a document to prove
    // it. The OPL002 and OPL003 fixes each pick a free field name from the document as it is, so two fixes
    // batched together could pick the same name, and they offer no fix-all at all.
    public class FixAllProviderTests
    {
        [Fact]
        public void ObjectPoolCodeFixProvider_UsesBatchFixer()
        {
            Assert.Same(WellKnownFixAllProviders.BatchFixer, new ObjectPoolCodeFixProvider().GetFixAllProvider());
        }

        [Fact]
        public void HiddenAllocationCodeFixProvider_OffersNoFixAll()
        {
            Assert.Null(new HiddenAllocationCodeFixProvider().GetFixAllProvider());
        }

        [Fact]
        public void UnityApiAllocationCodeFixProvider_OffersNoFixAll()
        {
            Assert.Null(new UnityApiAllocationCodeFixProvider().GetFixAllProvider());
        }

        // A new code fix provider fails this until a test above decides its fix-all behavior.
        [Fact]
        public void EveryCodeFixProvider_IsCovered()
        {
            var providers = typeof(ObjectPoolCodeFixProvider).Assembly.GetTypes()
                .Where(type => typeof(CodeFixProvider).IsAssignableFrom(type) && !type.IsAbstract)
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal);

            Assert.Equal(
                new[] { "HiddenAllocationCodeFixProvider", "ObjectPoolCodeFixProvider", "UnityApiAllocationCodeFixProvider" },
                providers);
        }
    }
}
