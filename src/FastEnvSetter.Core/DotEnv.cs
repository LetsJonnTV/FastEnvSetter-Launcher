using System.Text;

namespace FastEnvSetter.Core;

public sealed record DotEnvEntry(string Name, string Value);

/// <param name="SkippedLines">Zeilennummern, die nicht lesbar waren. Inhalte werden bewusst nicht weitergegeben.</param>
public sealed record DotEnvDocument(IReadOnlyList<DotEnvEntry> Entries, IReadOnlyList<int> SkippedLines);

public static class DotEnv
{
    private static readonly string[] SecretSuffixes = ["_KEY", "_TOKEN", "_SECRET", "_PASSWORD"];
    private static readonly string[] SecretNames = ["KEY", "TOKEN", "SECRET", "PASSWORD"];

    public static bool LooksSecret(string name)
    {
        var upper = name.ToUpperInvariant();
        return SecretNames.Contains(upper) || SecretSuffixes.Any(upper.EndsWith);
    }

    public static DotEnvDocument Parse(string text)
    {
        var entries = new List<DotEnvEntry>();
        var skipped = new List<int>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var equals = line.IndexOf('=');
            if (equals <= 0 || !IsValidName(line[..equals].TrimEnd()))
            {
                skipped.Add(lineNumber);
                continue;
            }

            var name = line[..equals].TrimEnd();
            var rest = line[(equals + 1)..].TrimStart();
            if (rest.Length > 0 && rest[0] is '"' or '\'')
            {
                var value = ReadQuoted(rest, lines, ref i);
                if (value is null)
                    skipped.Add(lineNumber);
                else
                    Add(name, value);
            }
            else
            {
                Add(name, StripInlineComment(rest));
            }
        }

        return new DotEnvDocument(entries, skipped);

        void Add(string name, string value)
        {
            // Wie in gängigen Parsern gewinnt der letzte Eintrag.
            entries.RemoveAll(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            entries.Add(new DotEnvEntry(name, value));
        }
    }

    public static string Serialize(IEnumerable<DotEnvEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
            builder.Append(entry.Name).Append('=').Append(Format(entry.Value)).Append('\n');
        return builder.ToString();
    }

    private static string Format(string value)
    {
        if (value.All(c => char.IsLetterOrDigit(c) || "_-./:@,+".Contains(c)))
            return value;

        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return '"' + escaped + '"';
    }

    private static bool IsValidName(string name) =>
        (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-');

    private static string StripInlineComment(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '#' && char.IsWhiteSpace(value[i - 1]))
                return value[..i].TrimEnd();
        }

        return value.StartsWith('#') ? "" : value.TrimEnd();
    }

    /// <returns>Der Wert oder null, wenn das schließende Anführungszeichen fehlt.</returns>
    private static string? ReadQuoted(string first, string[] lines, ref int index)
    {
        var quote = first[0];
        var builder = new StringBuilder();
        var current = first[1..];
        var lineIndex = index;

        while (true)
        {
            for (var i = 0; i < current.Length; i++)
            {
                var c = current[i];
                if (c == quote)
                {
                    index = lineIndex;
                    return builder.ToString();
                }

                if (c == '\\' && quote == '"' && i + 1 < current.Length)
                {
                    i++;
                    builder.Append(current[i] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        var other => other,
                    });
                    if (current[i] is not ('n' or 'r' or 't' or '"' or '\\'))
                        builder.Insert(builder.Length - 1, '\\');
                }
                else
                {
                    builder.Append(c);
                }
            }

            if (++lineIndex >= lines.Length)
                return null;
            builder.Append('\n');
            current = lines[lineIndex];
        }
    }
}

public static class ServiceGraph
{
    /// <summary>Sortiert Dienste so, dass Abhängigkeiten zuerst kommen; sonst bleibt die Listenreihenfolge.</summary>
    /// <returns>Die Reihenfolge oder null bei einem Zyklus.</returns>
    public static IReadOnlyList<ServiceDefinition>? Order(IReadOnlyList<ServiceDefinition> services)
    {
        var result = new List<ServiceDefinition>();
        var remaining = services.ToList();
        var known = services.Select(s => s.Id).ToHashSet();

        while (remaining.Count > 0)
        {
            var done = result.Select(s => s.Id).ToHashSet();
            var next = remaining.FirstOrDefault(s => s.DependsOn.Where(known.Contains).All(done.Contains));
            if (next is null)
                return null;

            result.Add(next);
            remaining.Remove(next);
        }

        return result;
    }
}
