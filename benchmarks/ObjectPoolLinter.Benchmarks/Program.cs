using System.Globalization;
using BenchmarkDotNet.Running;

namespace ObjectPoolLinter.Benchmarks
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--analyzer-times")
            {
                var files = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 500;
                await AnalyzerTimes.RunAsync(files).ConfigureAwait(false);
                return;
            }

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
