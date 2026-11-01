using System.Text;

namespace ZenTune.Core.Games;

/// <summary>
/// A small reader for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf, loginusers.vdf).
/// Every node is either a string value or a block of child nodes. Keys are case-insensitive.
/// </summary>
public sealed class VdfNode
{
    public string? Value { get; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string? value = null) => Value = value;

    public VdfNode? this[string key] => Children.TryGetValue(key, out var n) ? n : null;

    /// <summary>The string value of a child, or "" when missing.</summary>
    public string Get(string key) => this[key]?.Value ?? "";

    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new VdfNode();
        ReadBlock(tokens, root);
        return root;
    }

    public static VdfNode? Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static void ReadBlock(IEnumerator<string> tokens, VdfNode parent)
    {
        while (tokens.MoveNext())
        {
            var key = tokens.Current;
            if (key == "}") return;
            if (!tokens.MoveNext()) return;

            var next = tokens.Current;
            if (next == "{")
            {
                var child = new VdfNode();
                ReadBlock(tokens, child);
                parent.Children[key] = child;
            }
            else
            {
                parent.Children[key] = new VdfNode(next);
            }
        }
    }

    /// <summary>Quoted strings (with \\ and \" escapes), bare words and braces. Skips // comments and [conditions].</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c == '[')
            {
                while (i < text.Length && text[i] != ']') i++;
                i++;
                continue;
            }

            if (c == '{' || c == '}')
            {
                yield return c.ToString();
                i++;
                continue;
            }

            var sb = new StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        char e = text[i + 1];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i++]);
                }
                i++; // closing quote
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
                    sb.Append(text[i++]);
            }
            yield return sb.ToString();
        }
    }
}
