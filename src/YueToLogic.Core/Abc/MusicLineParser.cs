using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using YueToLogic.Core.Harmony;

namespace YueToLogic.Core.Abc;

/// <summary>
/// Splits one ABC music line into tokens. It recognizes the YuE2 dialect and, so that nothing is
/// silently misread, also the common ABC constructs outside it, which become <see cref="UnsupportedToken"/>s.
/// </summary>
internal static class MusicLineParser
{
    public static IReadOnlyList<AbcToken> Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = new List<AbcToken>();
        var i = 0;
        while (i < line.Length)
        {
            var start = i;
            var c = line[i];
            if (char.IsWhiteSpace(c) || c == '`')
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                {
                    var end = line.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        tokens.Add(new UnsupportedToken(start, line[start..], "unterminated chord symbol"));
                        return tokens;
                    }

                    tokens.Add(new ChordSymbolToken(start, line[(start + 1)..end]));
                    i = end + 1;
                    break;
                }

                case '|':
                    i++;
                    if (i < line.Length && line[i] == ':')
                    {
                        tokens.Add(new UnsupportedToken(start, "|:", "repeat sign"));
                        i++;
                    }

                    while (i < line.Length && line[i] is '|' or ']')
                    {
                        i++;
                    }

                    tokens.Add(new BarLineToken(start));
                    break;

                case '[':
                    i = ReadBracket(line, start, tokens);
                    break;

                case '{':
                    i = SkipUntil(line, start, '}', "grace notes", tokens);
                    break;

                case '!' or '+':
                    i = SkipUntil(line, start, c, "decoration", tokens);
                    break;

                case '(':
                    i++;
                    while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] == ':'))
                    {
                        i++;
                    }

                    tokens.Add(new UnsupportedToken(start, line[start..i], "tuplet or slur"));
                    break;

                case 'z' or 'x':
                    i++;
                    tokens.Add(new RestToken(start, ReadLength(line, ref i)));
                    break;

                case 'Z' or 'X':
                    i++;
                    tokens.Add(new MeasureRestToken(start, ReadInt(line, ref i) ?? 1));
                    break;

                default:
                    if (TryReadNote(line, ref i, out var note))
                    {
                        tokens.Add(note);
                    }
                    else
                    {
                        i = start + 1;
                        tokens.Add(new UnsupportedToken(start, c.ToString(), DescribeUnexpected(c)));
                    }

                    break;
            }
        }

        return tokens;
    }

    private static int ReadBracket(string line, int start, List<AbcToken> tokens)
    {
        if (line.AsSpan(start).StartsWith("[K:", StringComparison.Ordinal))
        {
            var end = line.IndexOf(']', start);
            if (end < 0)
            {
                tokens.Add(new UnsupportedToken(start, line[start..], "unterminated inline key change"));
                return line.Length;
            }

            tokens.Add(new InlineKeyToken(start, line[(start + 3)..end].Trim()));
            return end + 1;
        }

        if (start + 1 < line.Length && line[start + 1] == '|')
        {
            tokens.Add(new BarLineToken(start));
            return start + 2;
        }

        var reason = start + 1 < line.Length && char.IsAsciiDigit(line[start + 1])
            ? "alternate ending"
            : "chord stack or inline field";
        return SkipUntil(line, start, ']', reason, tokens);
    }

    private static int SkipUntil(string line, int start, char terminator, string reason, List<AbcToken> tokens)
    {
        var end = line.IndexOf(terminator, start + 1);
        var next = end < 0 ? line.Length : end + 1;
        tokens.Add(new UnsupportedToken(start, line[start..next], reason));
        return next;
    }

    private static bool TryReadNote(string line, ref int i, [NotNullWhen(true)] out NoteToken? token)
    {
        token = null;
        var j = i;
        int? accidental = null;
        if (line.AsSpan(j).StartsWith("^^", StringComparison.Ordinal))
        {
            accidental = 2;
            j += 2;
        }
        else if (line.AsSpan(j).StartsWith("__", StringComparison.Ordinal))
        {
            accidental = -2;
            j += 2;
        }
        else if (line[j] is '^' or '_' or '=')
        {
            accidental = line[j] switch { '^' => 1, '_' => -1, _ => 0 };
            j++;
        }

        if (j >= line.Length || char.ToUpperInvariant(line[j]) is < 'A' or > 'G')
        {
            return false;
        }

        var raw = line[j++];
        var letter = char.ToUpperInvariant(raw);
        var pitch = 60 + NoteNames.NaturalSemitone(letter) + (char.IsLower(raw) ? 12 : 0);
        while (j < line.Length && line[j] is '\'' or ',')
        {
            pitch += line[j] == '\'' ? 12 : -12;
            j++;
        }

        var length = ReadLength(line, ref j);
        var tied = j < line.Length && line[j] == '-';
        if (tied)
        {
            j++;
        }

        token = new NoteToken(i, letter, pitch, accidental, length, tied);
        i = j;
        return true;
    }

    private static AbcLength ReadLength(string line, ref int i)
    {
        var numerator = ReadInt(line, ref i) ?? 1;
        var denominator = 1;
        while (i < line.Length && line[i] == '/')
        {
            i++;
            denominator *= ReadInt(line, ref i) ?? 2;
        }

        return new AbcLength(numerator, denominator);
    }

    private static int? ReadInt(string line, ref int i)
    {
        var start = i;
        while (i < line.Length && char.IsAsciiDigit(line[i]))
        {
            i++;
        }

        if (i == start)
        {
            return null;
        }

        return int.TryParse(line.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
    }

    private static string DescribeUnexpected(char c) => c switch
    {
        ')' => "slur",
        '>' or '<' => "broken rhythm",
        '-' => "tie without a preceding note",
        ':' => "repeat sign",
        '.' or '~' or 'H' or 'L' or 'M' or 'O' or 'P' or 'S' or 'T' or 'u' or 'v' => "decoration",
        _ => "unexpected character",
    };
}
