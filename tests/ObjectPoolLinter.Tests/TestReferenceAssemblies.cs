using System;
using System.IO;
using Microsoft.CodeAnalysis.Testing;

namespace ObjectPoolLinter.Tests
{
    // The framework every test compiles its source against. The analyzer runs inside Unity, so the
    // default is the oldest profile it claims to support: .NET Standard 2.1, the default API
    // compatibility level of Unity 2021.3 (the minimum the UPM package declares). That profile has no
    // DefaultInterpolatedStringHandler, so interpolation boxes its holes the way it does in a Unity
    // project. Setting OPL_TEST_REFERENCE_ASSEMBLIES=newest reruns the suite against the newest .NET
    // reference assemblies instead; CI runs both.
    internal static class TestReferenceAssemblies
    {
        public const string EnvironmentVariable = "OPL_TEST_REFERENCE_ASSEMBLIES";

        public static readonly ReferenceAssemblies Oldest = ReferenceAssemblies.NetStandard.NetStandard21;

        public static readonly ReferenceAssemblies Newest = new ReferenceAssemblies(
            "net10.0",
            new PackageIdentity("Microsoft.NETCore.App.Ref", "10.0.0"),
            Path.Combine("ref", "net10.0"));

        public static bool IsNewest { get; } = string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            "newest",
            StringComparison.OrdinalIgnoreCase);

        public static ReferenceAssemblies Default { get; } = IsNewest ? Newest : Oldest;
    }
}
