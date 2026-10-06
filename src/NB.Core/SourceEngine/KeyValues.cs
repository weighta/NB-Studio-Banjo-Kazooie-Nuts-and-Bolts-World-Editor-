using System.Text;

namespace NB.Core.SourceEngine;

/// <summary>
/// One block of Valve's KeyValues text format (VMF, VMT, ...): <c>name { "key" "value" ... child { ... } }</c>.
/// Keys keep their file order; a key can repeat (lookups return the first). Comments (<c>//</c>) are skipped,
/// quoted strings may contain spaces, unquoted tokens end at whitespace or a brace.
/// </summary>
public sealed class KvNode
{
    public string Name = "";
    public readonly List<KeyValuePair<string, string>> Values = new();
    public readonly List<KvNode> Children = new();

    public string? this[string key]
    {
        get
        {
            foreach (var kv in Values) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }

    public string Get(string key, string fallback = "") => this[key] ?? fallback;

    public IEnumerable<KvNode> All(string name) => Children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public KvNode? First(string name) => Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} ({Values.Count} values, {Children.Count} children)";
}

public static class KeyValues
{
    /// <summary>Parses a whole file: a root node (no name) whose children are the file's top-level blocks.</summary>
    public static KvNode Parse(string text)
    {
        var root = new KvNode { Name = "" };
        var stack = new Stack<KvNode>();
        stack.Push(root);
        int i = 0, line = 1;
        string? pending = null;   // a token waiting for its value or block
        while (true)
        {
            var (tok, quoted) = Next(text, ref i, ref line);
            if (tok == null) break;
            if (!quoted && tok == "{")
            {
                var node = new KvNode { Name = pending ?? "" };
                stack.Peek().Children.Add(node);
                stack.Push(node);
                pending = null;
            }
            else if (!quoted && tok == "}")
            {
                if (pending != null) throw new InvalidDataException($"KeyValues line {line}: key '{pending}' has no value");
                if (stack.Count == 1) throw new InvalidDataException($"KeyValues line {line}: unbalanced '}}'");
                stack.Pop();
            }
            else if (pending == null) pending = tok;
            else
            {
                // conditional suffixes ("[$X360]") after a value are skipped
                stack.Peek().Values.Add(new(pending, tok));
                pending = null;
                int save = i, saveLine = line;
                var (peek, pq) = Next(text, ref i, ref line);
                if (peek == null || pq || !(peek.StartsWith('[') && peek.EndsWith(']'))) { i = save; line = saveLine; }
            }
        }
        if (stack.Count != 1) throw new InvalidDataException($"KeyValues: {stack.Count - 1} block(s) not closed at the end of the file");
        return root;
    }

    public static KvNode Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    static (string? Token, bool Quoted) Next(string s, ref int i, ref int line)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (i >= s.Length) return (null, false);
        if (s[i] == '{' || s[i] == '}') return (s[i++].ToString(), false);
        if (s[i] == '"')
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length && s[i] != '"')
            {
                // no escape sequences: Hammer writes values verbatim (paths keep their backslashes)
                if (s[i] == '\n') line++;
                sb.Append(s[i++]);
            }
            i++;   // closing quote
            return (sb.ToString(), true);
        }
        int st = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') i++;
        return (s[st..i], false);
    }
}
