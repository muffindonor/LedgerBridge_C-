using System.Text;

namespace LedgerBridge.Cobol;

/// <summary>
/// Parses a fixed-format COBOL copybook into a <see cref="RecordLayout"/>.
/// Anything that would change field offsets and isn't supported (REDEFINES, OCCURS)
/// fails loudly, because a silently wrong offset corrupts every value after it.
/// </summary>
public static class CopybookParser
{
    private const int SequenceColumns = 6;
    private const int LastCodeColumn = 72;
    private const int MaxDigits = 18;

    private sealed record Statement(int Line, List<string> Tokens);

    private sealed record OpenGroup(int Level, string Name, int Offset);

    private sealed class Clauses
    {
        public string? Picture;
        public string Usage = "DISPLAY";
        public bool SignLeading;
        public bool SignTrailing;
        public bool SignSeparate;
    }

    public static RecordLayout Parse(string source)
    {
        var fields = new List<Field>();
        var byName = new Dictionary<string, Field>(StringComparer.Ordinal);
        var groups = new Stack<OpenGroup>();
        string? recordName = null;
        int? lastElementaryLevel = null;
        int offset = 0;

        foreach (Statement statement in ReadStatements(source))
        {
            int line = statement.Line;
            List<string> tokens = statement.Tokens;
            if (!int.TryParse(tokens[0], out int level))
            {
                throw new CopybookException($"Expected a level number but found '{tokens[0]}'.", line);
            }

            if (level == 88)
            {
                continue;
            }

            if (level == 66 || level == 77 || level is < 1 or > 49)
            {
                throw new CopybookException($"Level {level} is not supported.", line);
            }

            bool hasName = tokens.Count > 1 && !IsClauseKeyword(tokens[1]);
            string name = hasName ? tokens[1] : "FILLER";
            Clauses clauses = ParseClauses(tokens, hasName ? 2 : 1, line);

            if (lastElementaryLevel is { } previous && level > previous)
            {
                throw new CopybookException("An item with a PICTURE can't have subordinate items.", line);
            }

            while (groups.Count > 0 && groups.Peek().Level >= level)
            {
                CloseGroup(groups.Pop(), offset, fields, byName, line);
            }

            if (level == 1)
            {
                if (recordName is not null)
                {
                    throw new CopybookException("Only one 01 record per copybook is supported.", line);
                }

                recordName = name;
            }
            else if (recordName is null)
            {
                throw new CopybookException("The first item must be level 01.", line);
            }

            if (clauses.Picture is null)
            {
                if (clauses.Usage != "DISPLAY")
                {
                    throw new CopybookException("USAGE on a group item is not supported.", line);
                }

                groups.Push(new OpenGroup(level, name, offset));
                lastElementaryLevel = null;
                continue;
            }

            Field field = BuildField(name, offset, clauses, line);
            lastElementaryLevel = level;
            offset += field.Length;
            Add(field, fields, byName, line);
        }

        while (groups.Count > 0)
        {
            CloseGroup(groups.Pop(), offset, fields, byName, 0);
        }

        if (recordName is null)
        {
            throw new CopybookException("No record definition found.", 0);
        }

        fields.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : b.Length.CompareTo(a.Length));
        return new RecordLayout(recordName, offset, fields, byName);
    }

    private static void CloseGroup(
        OpenGroup group, int offset, List<Field> fields, Dictionary<string, Field> byName, int line)
    {
        // The 01 level names the record, and is reported as the layout rather than a field.
        if (group.Level == 1)
        {
            return;
        }

        Add(new Field(group.Name, group.Offset, offset - group.Offset, FieldKind.Text, 0, 0, false), fields, byName, line);
    }

    private static void Add(Field field, List<Field> fields, Dictionary<string, Field> byName, int line)
    {
        if (field.Name == "FILLER")
        {
            return;
        }

        if (!byName.TryAdd(field.Name, field))
        {
            throw new CopybookException($"Duplicate field name {field.Name}.", line);
        }

        fields.Add(field);
    }

    private static IEnumerable<Statement> ReadStatements(string source)
    {
        var tokens = new List<string>();
        int startLine = 0;
        int lineNumber = 0;

        foreach (string rawLine in source.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            if (rawLine.Length <= SequenceColumns || rawLine[SequenceColumns] is '*' or '/')
            {
                continue;
            }

            string code = rawLine[(SequenceColumns + 1)..Math.Min(rawLine.Length, LastCodeColumn)];
            foreach ((string token, bool endsStatement) in Tokenize(code, lineNumber))
            {
                if (tokens.Count == 0)
                {
                    startLine = lineNumber;
                }

                if (token.Length > 0)
                {
                    tokens.Add(token);
                }

                if (endsStatement)
                {
                    if (tokens.Count == 0)
                    {
                        throw new CopybookException("Empty statement.", lineNumber);
                    }

                    yield return new Statement(startLine, tokens);
                    tokens = [];
                }
            }
        }

        if (tokens.Count > 0)
        {
            throw new CopybookException("The last statement has no terminating period.", startLine);
        }
    }

    private static IEnumerable<(string Token, bool EndsStatement)> Tokenize(string code, int line)
    {
        var results = new List<(string, bool)>();
        var current = new StringBuilder();
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '"' or '\'')
            {
                int close = code.IndexOf(c, i + 1);
                if (close < 0)
                {
                    throw new CopybookException("Unterminated string literal.", line);
                }

                current.Append(code, i, close - i + 1);
                i = close;
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush(current, results);
            }
            else if (c == '.' && (i + 1 == code.Length || char.IsWhiteSpace(code[i + 1])))
            {
                results.Add((current.ToString(), true));
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush(current, results);
        return results;

        static void Flush(StringBuilder builder, List<(string, bool)> output)
        {
            if (builder.Length > 0)
            {
                output.Add((builder.ToString(), false));
                builder.Clear();
            }
        }
    }

    private static bool IsClauseKeyword(string token) =>
        token.ToUpperInvariant() is "PIC" or "PICTURE" or "USAGE" or "SIGN" or "VALUE" or "REDEFINES" or "OCCURS"
            or "COMP" or "COMP-3" or "COMP-4" or "COMPUTATIONAL" or "COMPUTATIONAL-3" or "COMPUTATIONAL-4"
            or "BINARY" or "PACKED-DECIMAL" or "DISPLAY" or "LEADING" or "TRAILING";

    private static Clauses ParseClauses(List<string> tokens, int start, int line)
    {
        var clauses = new Clauses();
        int i = start;
        while (i < tokens.Count)
        {
            string token = tokens[i].ToUpperInvariant();
            i++;
            switch (token)
            {
                case "PIC" or "PICTURE":
                    SkipIs(tokens, ref i);
                    clauses.Picture = Next(tokens, ref i, line, "a picture string").ToUpperInvariant();
                    break;
                case "USAGE":
                    SkipIs(tokens, ref i);
                    clauses.Usage = NormalizeUsage(Next(tokens, ref i, line, "a usage").ToUpperInvariant(), line);
                    break;
                case "COMP" or "COMP-3" or "COMP-4" or "COMPUTATIONAL" or "COMPUTATIONAL-3" or "COMPUTATIONAL-4"
                    or "BINARY" or "PACKED-DECIMAL" or "DISPLAY":
                    clauses.Usage = NormalizeUsage(token, line);
                    break;
                case "SIGN":
                    SkipIs(tokens, ref i);
                    ParseSign(Next(tokens, ref i, line, "LEADING or TRAILING").ToUpperInvariant(), tokens, ref i, clauses, line);
                    break;
                case "LEADING" or "TRAILING":
                    ParseSign(token, tokens, ref i, clauses, line);
                    break;
                case "VALUE":
                    SkipIs(tokens, ref i);
                    Next(tokens, ref i, line, "a value");
                    break;
                default:
                    throw new CopybookException($"Clause '{tokens[i - 1]}' is not supported.", line);
            }
        }

        return clauses;
    }

    private static void SkipIs(List<string> tokens, ref int i)
    {
        if (i < tokens.Count && tokens[i].Equals("IS", StringComparison.OrdinalIgnoreCase))
        {
            i++;
        }
    }

    private static string Next(List<string> tokens, ref int i, int line, string expected)
    {
        if (i >= tokens.Count)
        {
            throw new CopybookException($"Expected {expected}.", line);
        }

        return tokens[i++];
    }

    private static void ParseSign(string position, List<string> tokens, ref int i, Clauses clauses, int line)
    {
        if (position == "LEADING")
        {
            clauses.SignLeading = true;
        }
        else if (position == "TRAILING")
        {
            clauses.SignTrailing = true;
        }
        else
        {
            throw new CopybookException($"Expected LEADING or TRAILING but found '{position}'.", line);
        }

        if (i < tokens.Count && tokens[i].Equals("SEPARATE", StringComparison.OrdinalIgnoreCase))
        {
            clauses.SignSeparate = true;
            i++;
            if (i < tokens.Count && tokens[i].Equals("CHARACTER", StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }
        }
    }

    private static string NormalizeUsage(string usage, int line) => usage switch
    {
        "DISPLAY" => "DISPLAY",
        "COMP-3" or "COMPUTATIONAL-3" or "PACKED-DECIMAL" => "PACKED",
        "COMP" or "COMPUTATIONAL" or "COMP-4" or "COMPUTATIONAL-4" or "BINARY" => "BINARY",
        _ => throw new CopybookException($"Usage {usage} is not supported.", line),
    };

    private static Field BuildField(string name, int offset, Clauses clauses, int line)
    {
        (bool isText, int length, int digits, int scale, bool signed) = ParsePicture(clauses.Picture!, line);
        bool anySign = clauses.SignLeading || clauses.SignTrailing;

        if (isText)
        {
            if (clauses.Usage != "DISPLAY" || anySign)
            {
                throw new CopybookException("USAGE and SIGN clauses need a numeric picture.", line);
            }

            return new Field(name, offset, length, FieldKind.Text, 0, 0, false);
        }

        if (anySign && !signed)
        {
            throw new CopybookException("SIGN clause on an unsigned picture.", line);
        }

        switch (clauses.Usage)
        {
            case "PACKED":
                return new Field(name, offset, (digits / 2) + 1, FieldKind.Packed, digits, scale, signed);
            case "BINARY":
                return new Field(name, offset, BinaryLength(digits), FieldKind.Binary, digits, scale, signed);
        }

        if (clauses.SignLeading && !clauses.SignSeparate)
        {
            throw new CopybookException("SIGN LEADING without SEPARATE is not supported.", line);
        }

        if (clauses.SignSeparate)
        {
            FieldKind kind = clauses.SignLeading ? FieldKind.DisplayLeadingSeparate : FieldKind.DisplayTrailingSeparate;
            return new Field(name, offset, digits + 1, kind, digits, scale, true);
        }

        return new Field(name, offset, digits, FieldKind.Display, digits, scale, signed);
    }

    // Mainframe sizing. GnuCOBOL's default gives 1-2 digit fields a single byte,
    // so those are avoided in the test fixtures that compare the two.
    private static int BinaryLength(int digits) => digits switch
    {
        <= 4 => 2,
        <= 9 => 4,
        _ => 8,
    };

    private static (bool IsText, int Length, int Digits, int Scale, bool Signed) ParsePicture(string picture, int line)
    {
        string expanded = ExpandPicture(picture, line);
        if (expanded.All(c => c is 'X' or 'A'))
        {
            return (true, expanded.Length, 0, 0, false);
        }

        bool signed = expanded[0] == 'S';
        string body = signed ? expanded[1..] : expanded;
        int point = body.IndexOf('V');
        string digitsOnly = point < 0 ? body : body.Remove(point, 1);
        if (digitsOnly.Length == 0 || digitsOnly.Any(c => c != '9'))
        {
            throw new CopybookException($"Picture '{picture}' is not supported.", line);
        }

        if (digitsOnly.Length > MaxDigits)
        {
            throw new CopybookException($"Picture '{picture}' has more than {MaxDigits} digits.", line);
        }

        int scale = point < 0 ? 0 : body.Length - point - 1;
        return (false, digitsOnly.Length, digitsOnly.Length, scale, signed);
    }

    private static string ExpandPicture(string picture, int line)
    {
        var expanded = new StringBuilder();
        for (int i = 0; i < picture.Length; i++)
        {
            char c = picture[i];
            if (i + 1 < picture.Length && picture[i + 1] == '(')
            {
                int close = picture.IndexOf(')', i + 2);
                if (close < 0 || !int.TryParse(picture.AsSpan(i + 2, close - i - 2), out int repeat) || repeat < 1)
                {
                    throw new CopybookException($"Bad repeat count in picture '{picture}'.", line);
                }

                expanded.Append(c, repeat);
                i = close;
            }
            else
            {
                expanded.Append(c);
            }
        }

        return expanded.ToString();
    }
}
