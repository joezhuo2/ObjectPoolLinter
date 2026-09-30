// Verifies that the .snupkg next to a .nupkg is a symbol package nuget.org will accept and that its
// PDBs actually belong to the assemblies the .nupkg ships.
//
//   dotnet run --file build/verify-symbols.cs -- [directory]
//
// The directory defaults to artifacts/nuget and must hold exactly one ObjectPoolLinter .nupkg and its
// .snupkg (run `dotnet pack` first). The checks mirror nuget.org's symbol package validation, which only
// runs after a push, so a bad .snupkg fails CI instead of being rejected after the .nupkg is already
// public:
//
//   - the .snupkg has the same id and version as the .nupkg and declares the SymbolsPackage type;
//   - it contains nothing but .pdb files (plus the package metadata every .nupkg has);
//   - every .dll in the .nupkg has a .pdb at the same path in the .snupkg;
//   - every .pdb is a portable PDB (nuget.org rejects Windows PDBs);
//   - every .pdb's id matches the CodeView debug directory entry of its .dll, so a debugger will load it;
//   - every .pdb carries Source Link, so stepping in resolves source from GitHub.
//
// It exits 1 and lists every failure, and on GitHub Actions appends the result to the job summary.

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

var directory = args.Length > 0 ? args[0] : Path.Combine("artifacts", "nuget");
var errors = new List<string>();
var verified = new List<string>();

var nupkgs = Directory.GetFiles(directory, "*.nupkg");
var snupkgs = Directory.GetFiles(directory, "*.snupkg");
if (nupkgs.Length != 1 || snupkgs.Length != 1)
{
    Console.Error.WriteLine(
        $"Expected exactly one .nupkg and one .snupkg in '{directory}', found {nupkgs.Length} and {snupkgs.Length}.");
    return 1;
}

using (var package = ZipFile.OpenRead(nupkgs[0]))
using (var symbols = ZipFile.OpenRead(snupkgs[0]))
{
    var (packageId, packageVersion, _) = ReadNuspec(package);
    var (symbolsId, symbolsVersion, symbolsTypes) = ReadNuspec(symbols);

    if (!string.Equals(packageId, symbolsId, StringComparison.OrdinalIgnoreCase) || packageVersion != symbolsVersion)
        errors.Add($".snupkg is {symbolsId} {symbolsVersion} but the .nupkg is {packageId} {packageVersion}.");
    if (!symbolsTypes.Contains("SymbolsPackage"))
        errors.Add(".snupkg nuspec does not declare <packageType name=\"SymbolsPackage\" />.");

    foreach (var entry in symbols.Entries)
    {
        if (IsPackageMetadata(entry.FullName) || entry.FullName.EndsWith('/'))
            continue;
        if (!entry.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            errors.Add($".snupkg contains '{entry.FullName}'; symbol packages may only contain .pdb files.");
    }

    var dlls = package.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
    if (dlls.Count == 0)
        errors.Add(".nupkg contains no .dll files.");

    foreach (var dll in dlls)
    {
        var pdbPath = Path.ChangeExtension(dll.FullName, ".pdb");
        var pdb = symbols.GetEntry(pdbPath);
        if (pdb is null)
        {
            errors.Add($"{dll.FullName}: no {pdbPath} in the .snupkg.");
            continue;
        }

        var failure = VerifyPdb(ReadAll(dll), ReadAll(pdb));
        if (failure is null)
            verified.Add(dll.FullName);
        else
            errors.Add($"{dll.FullName}: {failure}");
    }

    Console.WriteLine($"{Path.GetFileName(snupkgs[0])} ({symbolsId} {symbolsVersion})");
}

foreach (var path in verified)
    Console.WriteLine($"  ok    {path}: portable PDB, id matches, Source Link present");
foreach (var error in errors)
    Console.WriteLine($"  FAIL  {error}");

var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(summaryPath))
{
    var lines = new List<string> { "### Symbol package", "" };
    lines.AddRange(verified.Select(p => $"- :white_check_mark: `{p}`"));
    lines.AddRange(errors.Select(e => $"- :x: {e}"));
    lines.Add("");
    File.AppendAllLines(summaryPath, lines);
}

if (errors.Count > 0)
{
    foreach (var error in errors)
        Console.WriteLine($"::error::{error}");
    return 1;
}

Console.WriteLine($"Symbol package valid: {verified.Count} assemblies verified.");
return 0;

static (string Id, string Version, HashSet<string> PackageTypes) ReadNuspec(ZipArchive archive)
{
    var entry = archive.Entries.Single(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    using var stream = entry.Open();
    var metadata = XDocument.Load(stream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
    string Value(string name) => metadata.Elements().Single(e => e.Name.LocalName == name).Value;
    var types = metadata.Descendants()
        .Where(e => e.Name.LocalName == "packageType")
        .Select(e => (string?)e.Attribute("name") ?? "")
        .ToHashSet(StringComparer.Ordinal);
    return (Value("id"), Value("version"), types);
}

static bool IsPackageMetadata(string path) =>
    path == "[Content_Types].xml"
    || path.StartsWith("_rels/", StringComparison.Ordinal)
    || path.StartsWith("package/", StringComparison.Ordinal)
    || (!path.Contains('/') && path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

static byte[] ReadAll(ZipArchiveEntry entry)
{
    using var stream = entry.Open();
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

static string? VerifyPdb(byte[] dllBytes, byte[] pdbBytes)
{
    using var pe = new PEReader(new MemoryStream(dllBytes));
    var codeView = pe.ReadDebugDirectory().Where(e => e.Type == DebugDirectoryEntryType.CodeView).ToList();
    if (codeView.Count != 1)
        return $"expected one CodeView debug directory entry, found {codeView.Count}.";
    if (!codeView[0].IsPortableCodeView)
        return "the assembly references a Windows PDB; nuget.org only accepts portable PDBs.";
    var expected = pe.ReadCodeViewDebugDirectoryData(codeView[0]);

    if (pdbBytes.Length < 4 || pdbBytes[0] != (byte)'B' || pdbBytes[1] != (byte)'S' || pdbBytes[2] != (byte)'J' || pdbBytes[3] != (byte)'B')
        return "the .pdb is not a portable PDB.";

    using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdbBytes));
    var reader = provider.GetMetadataReader();
    var id = new BlobContentId(reader.DebugMetadataHeader!.Id);
    var expectedId = new BlobContentId(expected.Guid, codeView[0].Stamp);
    if (id != expectedId)
        return $"the .pdb id {id.Guid}/{id.Stamp:X8} does not match the assembly's {expectedId.Guid}/{expectedId.Stamp:X8}; the .pdb is from a different build.";

    var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    var hasSourceLink = reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
        .Select(reader.GetCustomDebugInformation)
        .Any(info => reader.GetGuid(info.Kind) == sourceLinkKind);
    if (!hasSourceLink)
        return "the .pdb has no Source Link information.";

    return null;
}
