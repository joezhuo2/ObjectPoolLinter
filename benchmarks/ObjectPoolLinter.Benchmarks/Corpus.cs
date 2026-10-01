using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter.Benchmarks
{
    // A generated stand-in for a Unity project's scripts. Each file holds one MonoBehaviour and one plain
    // class, about 100 lines together. One file in four is a component with per-frame allocations in Update
    // that OPL001, OPL002, OPL003 and OPL009 report; the rest allocate only where nothing reports it (Awake,
    // cold methods, plain classes), as most scripts in a shipped game do. The analyzers' cost follows the code
    // they walk more than the code they report, so the size matters more than the mix.
    internal static class Corpus
    {
        private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp9);

        internal static ImmutableArray<SyntaxTree> Parse(int fileCount)
        {
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>(fileCount + 1);
            trees.Add(CSharpSyntaxTree.ParseText(Tests.SharedUnityStub.Source, ParseOptions, path: "UnityStub.cs"));

            for (var i = 0; i < fileCount; i++)
                trees.Add(CSharpSyntaxTree.ParseText(Script(i), ParseOptions, path: $"Assets/Scripts/Module{i % 10}/Script{i}.cs"));

            return trees.ToImmutable();
        }

        // The framework assemblies of the running .NET, standing in for the .NET Standard 2.1 profile Unity
        // compiles against: binding against either costs about the same.
        internal static ImmutableArray<MetadataReference> References()
        {
            var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? Array.Empty<string>();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.Runtime", "System.Private.CoreLib", "System.Collections", "System.Linq", "netstandard",
            };

            return paths
                .Where(path => wanted.Contains(Path.GetFileNameWithoutExtension(path)))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToImmutableArray();
        }

        internal static CSharpCompilation Compile(ImmutableArray<SyntaxTree> trees, ImmutableArray<MetadataReference> references) =>
            CSharpCompilation.Create(
                "Assembly-CSharp",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: true));

        // Every analyzer and suppressor the package ships. The [ObjectPool] generator runs before analysis,
        // only on classes marked [ObjectPool], and is not measured here.
        internal static ImmutableArray<DiagnosticAnalyzer> AllAnalyzers() =>
            typeof(ObjectPoolAnalyzer).Assembly.GetTypes()
                .Where(type => typeof(DiagnosticAnalyzer).IsAssignableFrom(type) && !type.IsAbstract &&
                               type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() != null)
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!)
                .ToImmutableArray();

        private static string Script(int i)
        {
            var builder = new StringBuilder();

            builder.Append(
                "using System.Collections.Generic;\n" +
                "using System.Linq;\n" +
                "using UnityEngine;\n" +
                "\n" +
                $"namespace Game.Module{i % 10}\n" +
                "{\n" +
                $"    public class Script{i} : MonoBehaviour\n" +
                "    {\n" +
                "        private readonly List<int> _scores = new List<int>();\n" +
                "        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();\n" +
                "        private Transform _target;\n" +
                "        private int _hits;\n" +
                "        private float _timer;\n" +
                "\n" +
                "        private void Awake()\n" +
                "        {\n" +
                "            _target = GetComponent<Transform>();\n" +
                $"            for (var k = 0; k < 16; k++) _scores.Add(k * {i % 7 + 1});\n" +
                $"            _names[0] = \"Script{i}\";\n" +
                "        }\n" +
                "\n" +
                "        private void Update()\n" +
                "        {\n" +
                "            _timer += 0.016f;\n" +
                "            var total = 0;\n" +
                "            for (var k = 0; k < _scores.Count; k++)\n" +
                "            {\n" +
                "                if (_scores[k] > _hits) total += _scores[k];\n" +
                "            }\n" +
                "            _hits = total % 100;\n");

            if (i % 4 == 0)
            {
                builder.Append(
                    "            var hits = new List<int>();\n" +
                    "            var label = \"Hits: \" + _hits;\n" +
                    "            var alive = _scores.Where(s => s > _hits).ToList();\n" +
                    "            if (gameObject.tag == \"Player\") hits.Add(alive.Count);\n" +
                    "            var body = GetComponent<Rigidbody>();\n");
            }

            builder.Append(
                "        }\n" +
                "\n" +
                "        private void FixedUpdate()\n" +
                "        {\n" +
                "            var sum = 0f;\n" +
                "            for (var k = 0; k < 8; k++) sum += k * _timer;\n" +
                "            _timer = sum > 1000f ? 0f : _timer;\n" +
                "        }\n" +
                "\n" +
                "        public string Describe(int bonus)\n" +
                "        {\n" +
                "            var parts = _scores.Select(s => (s + bonus).ToString()).ToList();\n" +
                "            return _names[0] + \": \" + string.Join(\", \", parts);\n" +
                "        }\n" +
                "    }\n" +
                "\n" +
                $"    public sealed class Inventory{i}\n" +
                "    {\n" +
                "        private readonly List<string> _items = new List<string>();\n" +
                "\n" +
                "        public void Add(string item)\n" +
                "        {\n" +
                "            if (!_items.Contains(item)) _items.Add(item);\n" +
                "        }\n" +
                "\n" +
                "        public IEnumerable<string> Sorted() => _items.OrderBy(item => item);\n" +
                "\n" +
                "        public int Count(char first)\n" +
                "        {\n" +
                "            var count = 0;\n" +
                "            foreach (var item in _items)\n" +
                "            {\n" +
                "                if (item.Length > 0 && item[0] == first) count++;\n" +
                "            }\n" +
                "            return count;\n" +
                "        }\n" +
                "\n" +
                "        public Dictionary<string, int> Histogram()\n" +
                "        {\n" +
                "            var result = new Dictionary<string, int>();\n" +
                "            foreach (var item in _items)\n" +
                "            {\n" +
                "                result.TryGetValue(item, out var seen);\n" +
                "                result[item] = seen + 1;\n" +
                "            }\n" +
                "            return result;\n" +
                "        }\n" +
                "    }\n" +
                "}\n");

            return builder.ToString();
        }
    }
}
