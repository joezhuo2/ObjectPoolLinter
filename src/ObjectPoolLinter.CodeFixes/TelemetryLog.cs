using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ObjectPoolLinter
{
    // The local telemetry file the code fixes count into when object_pool_linter.telemetry = true:
    //
    //   {
    //     "schema": 1,
    //     "codeFixes": { "ObjectPoolLinterCacheLambda": 3 },
    //     "fixAll": { "ObjectPoolLinterCacheLambda": 1 }
    //   }
    //
    // codeFixes counts each fix applied by itself, fixAll each Fix All run, both keyed by the fix's
    // equivalence key. The file holds nothing else: no file names, paths, symbols or code. Nothing reads it
    // but the user, who can attach it to an issue or delete it. See docs/telemetry.md.
    internal static class TelemetryLog
    {
        internal const string CodeFixesSection = "codeFixes";
        internal const string FixAllSection = "fixAll";

        private static readonly string[] Sections = { CodeFixesSection, FixAllSection };

        private static readonly Regex SectionPattern = new("\"(?<name>[A-Za-z]+)\"\\s*:\\s*\\{(?<body>[^}]*)\\}", RegexOptions.CultureInvariant);
        private static readonly Regex EntryPattern = new("\"(?<key>[^\"\\\\]+)\"\\s*:\\s*(?<count>\\d+)", RegexOptions.CultureInvariant);

        // Tests point this at a temporary file.
        internal static string? PathOverride { get; set; }

        internal static string FilePath => PathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObjectPoolLinter", "telemetry.json");

        // Never throws: a telemetry file that cannot be written must not get in the way of a fix. Another
        // IDE process holding the file is waited out for a moment; past that the count is dropped.
        internal static void Increment(string section, string key)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Update(section, key);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(20);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        internal static Dictionary<string, SortedDictionary<string, long>> Parse(string text)
        {
            var data = new Dictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
            foreach (var name in Sections)
                data[name] = new SortedDictionary<string, long>(StringComparer.Ordinal);

            foreach (Match section in SectionPattern.Matches(text))
            {
                if (!data.TryGetValue(section.Groups["name"].Value, out var entries)) continue;

                foreach (Match entry in EntryPattern.Matches(section.Groups["body"].Value))
                {
                    if (long.TryParse(entry.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                        entries[entry.Groups["key"].Value] = count;
                }
            }

            return data;
        }

        internal static string Format(Dictionary<string, SortedDictionary<string, long>> data)
        {
            var builder = new StringBuilder();
            builder.Append("{\n  \"schema\": 1");

            foreach (var name in Sections)
            {
                builder.Append(",\n  \"").Append(name).Append("\": {");

                var first = true;
                foreach (var pair in data[name])
                {
                    builder.Append(first ? "\n" : ",\n");
                    builder.Append("    \"").Append(pair.Key).Append("\": ").Append(pair.Value.ToString(CultureInfo.InvariantCulture));
                    first = false;
                }

                builder.Append(first ? "}" : "\n  }");
            }

            return builder.Append("\n}\n").ToString();
        }

        private static void Update(string section, string key)
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            string text;
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                text = reader.ReadToEnd();

            var data = Parse(text);
            data[section].TryGetValue(key, out var count);
            data[section][key] = count + 1;

            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Format(data));
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
