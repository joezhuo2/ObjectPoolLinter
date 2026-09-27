namespace ObjectPoolLinter.Tests
{
    // Test sources take the line endings of the checkout (LF or CRLF, per .gitattributes), so tests
    // that care about line endings set them explicitly.
    internal static class LineEndings
    {
        internal static string With(string text, string endOfLine) =>
            text.Replace("\r\n", "\n").Replace("\n", endOfLine);
    }
}
