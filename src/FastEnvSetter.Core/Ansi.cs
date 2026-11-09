using System.Text;
using System.Text.RegularExpressions;

namespace FastEnvSetter.Core;

/// <param name="Color">RGB-Wert oder null für die Standardfarbe.</param>
public sealed record LogSpan(string Text, int? Color);

public static partial class Ansi
{
    private static readonly int[] Palette =
    [
        0x484F58, 0xE5534B, 0x3FB950, 0xD29922, 0x58A6FF, 0xBC8CFF, 0x39C5CF, 0xB1BAC4,
        0x6E7681, 0xFF7B72, 0x56D364, 0xE3B341, 0x79C0FF, 0xD2A8FF, 0x56D4DD, 0xF0F6FC,
    ];

    /// <returns>Der Text ohne Steuerzeichen und seine Farbabschnitte; Spans ist null, wenn er keine enthielt.</returns>
    public static (string Plain, IReadOnlyList<LogSpan>? Spans) Parse(string raw)
    {
        if (!raw.Contains('\u001b'))
            return (raw, null);

        var plain = new StringBuilder();
        var spans = new List<LogSpan>();
        int? color = null;
        var position = 0;

        foreach (Match match in Sequence().Matches(raw))
        {
            Add(raw[position..match.Index]);
            position = match.Index + match.Length;
            if (match.Groups[2].Value == "m")
                color = Apply(match.Groups[1].Value, color);
        }

        Add(raw[position..]);
        return (plain.ToString(), spans);

        void Add(string text)
        {
            if (text.Length == 0)
                return;
            plain.Append(text);
            spans.Add(new LogSpan(text, color));
        }
    }

    private static int? Apply(string parameters, int? current)
    {
        var codes = parameters.Split(';').Select(p => int.TryParse(p, out var value) ? value : 0).ToArray();
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            if (code is 0 or 39)
            {
                current = null;
            }
            else if (code is >= 30 and <= 37)
            {
                current = Palette[code - 30];
            }
            else if (code is >= 90 and <= 97)
            {
                current = Palette[code - 90 + 8];
            }
            else if (code == 38 && i + 2 < codes.Length && codes[i + 1] == 5)
            {
                current = Extended(codes[i + 2]);
                i += 2;
            }
            else if (code == 38 && i + 4 < codes.Length && codes[i + 1] == 2)
            {
                current = (codes[i + 2] << 16) | (codes[i + 3] << 8) | codes[i + 4];
                i += 4;
            }
        }

        return current;
    }

    private static int Extended(int index)
    {
        if (index < 16)
            return Palette[Math.Max(index, 0)];
        if (index >= 232)
        {
            var gray = 8 + ((Math.Min(index, 255) - 232) * 10);
            return (gray << 16) | (gray << 8) | gray;
        }

        var cube = index - 16;
        return (Level(cube / 36) << 16) | (Level(cube / 6 % 6) << 8) | Level(cube % 6);

        static int Level(int step) => step == 0 ? 0 : 55 + (step * 40);
    }

    [GeneratedRegex(@"\x1b\[([0-9;?]*)([A-Za-z])")]
    private static partial Regex Sequence();
}
