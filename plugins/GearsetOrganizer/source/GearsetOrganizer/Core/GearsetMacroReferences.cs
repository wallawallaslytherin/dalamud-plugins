using System.Globalization;

namespace GearsetOrganizer.Core;

public sealed record GearsetMacroLineResult(
    string UpdatedLine,
    bool Changed,
    string? Blocker,
    string? NamedReference = null);

/// <summary>
/// Remaps only understood numeric references; this does not execute or persist macros.
/// Names need a separate before/after prefix-resolution audit: the game selects the
/// first matching set by number, so unchanged name text alone is not sufficient.
/// </summary>
public static class GearsetMacroReferences
{
    public static GearsetMacroLineResult AnalyzeLine(string line, IReadOnlyDictionary<int, int> numberMapping)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(numberMapping);
        if (numberMapping.Count > GearsetOrganization.Capacity
            || numberMapping.Any(pair => pair.Key is < 1 or > 100 || pair.Value is < 1 or > 100)
            || numberMapping.Values.Distinct().Count() != numberMapping.Count)
            return Block(line, "Gearset number mapping must have unique source and target numbers between 1 and 100.");

        // A macro line cannot contain another line or an opaque game payload. Do not let
        // string parsing turn such content into a different executable command.
        if (line.Any(character => char.IsControl(character) && character != '\t'))
            return Block(line, "Macro line contains a line break, control character, or opaque payload.");

        var first = line.AsSpan().TrimStart();
        var commandEnd = 0;
        while (commandEnd < first.Length && !char.IsWhiteSpace(first[commandEnd])) commandEnd++;
        var command = first[..commandEnd].ToString();
        var gearsetCommand = Is(command, "/gearset") || Is(command, "/gs");
        var iconCommand = Is(command, "/macroicon") || Is(command, "/micon");
        var hotbarCommand = Is(command, "/hotbar") || Is(command, "/crosshotbar")
            || Is(command, "/chotbar") || Is(command, "/xhb")
            || Is(command, "/pvphotbar") || Is(command, "/pvpcrosshotbar") || Is(command, "/pvpchotbar");
        if (!gearsetCommand && !iconCommand && !hotbarCommand) return Unchanged(line);

        var mentionsGearset = line.Contains("gearset", StringComparison.OrdinalIgnoreCase);
        if (hotbarCommand)
            return mentionsGearset
                ? Block(line, "Gearset hotbar-placement command requires a separate reference audit.")
                : Unchanged(line);

        if (!TryTokenize(line, out var tokens, out var problem))
            return gearsetCommand || mentionsGearset ? Block(line, problem!) : Unchanged(line);

        if (iconCommand)
        {
            if (tokens.Count < 3 || !Is(tokens[2].Value, "gearset"))
            {
                // The icon name itself may legitimately be "gearset" in another category.
                return tokens.Skip(2).Any(token => Is(token.Value, "gearset"))
                    ? Block(line, "Gearset icon category appears in an unsupported argument position.")
                    : Unchanged(line);
            }
            if (!ValidSuffix(tokens, 3, allowPlate: false))
                return Block(line, "Gearset icon has unsupported trailing arguments.");
            return RemapReference(line, tokens[1], numberMapping);
        }

        if (tokens.Count == 1) return Unchanged(line); // Opens the list without selecting a set.
        var referenceIndex = 1;
        if (Is(tokens[1].Value, "change") || Is(tokens[1].Value, "equip"))
        {
            referenceIndex = 2;
            if (tokens.Count <= referenceIndex) return Block(line, "Gearset selection is missing its number or name.");
        }
        else if (new[] { "view", "save", "delete", "reassign", "rename", "remove", "copy", "update", "move", "new" }
                     .Any(subcommand => Is(tokens[1].Value, subcommand))
                 || (!AsciiDigits(tokens[1].Value) && !tokens[1].Quoted))
        {
            return Block(line, "Unsupported or ambiguous gearset subcommand.");
        }

        if (!ValidSuffix(tokens, referenceIndex + 1, allowPlate: true))
            return Block(line, "Gearset selection has unsupported trailing arguments.");
        return RemapReference(line, tokens[referenceIndex], numberMapping);
    }

    private static GearsetMacroLineResult RemapReference(string line, Token reference, IReadOnlyDictionary<int, int> mapping)
    {
        var value = reference.Value;
        if (!AsciiDigits(value))
        {
            if (value.Length == 0 || value != value.Trim()
                || value.Any(character => character is '<' or '>' or ';' or '|' or '\\')
                || char.IsDigit(value[0]) || value[0] is '+' or '-'
                || (value[0] == '\'' && value[^1] == '\''))
                return Block(line, "Gearset reference contains an unsupported number, placeholder, or ambiguous syntax.");
            return new GearsetMacroLineResult(line, false, null, value);
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var oldNumber)
            || oldNumber is < 1 or > 100)
            return Block(line, "Gearset reference number must be between 1 and 100.");
        if (!mapping.TryGetValue(oldNumber, out var newNumber))
            return Block(line, $"Gearset reference {oldNumber} does not resolve to an existing saved set.");
        if (oldNumber == newNumber) return Unchanged(line);

        var updated = line[..reference.ContentStart] + newNumber.ToString(CultureInfo.InvariantCulture)
            + line[(reference.ContentStart + reference.ContentLength)..];
        return new GearsetMacroLineResult(updated, true, null);
    }

    private static bool ValidSuffix(IReadOnlyList<Token> tokens, int start, bool allowPlate)
    {
        if (allowPlate && start < tokens.Count && AsciiDigits(tokens[start].Value)) start++;
        if (start == tokens.Count) return true;
        // The native macro wait modifier is independent of which gearset is selected.
        return start + 1 == tokens.Count && !tokens[start].Quoted && ValidWait(tokens[start].Value);
    }

    private static bool ValidWait(string value)
    {
        if (!value.StartsWith("<wait.", StringComparison.OrdinalIgnoreCase) || !value.EndsWith('>')) return false;
        var number = value[6..^1];
        return number.Length > 0 && number.Count(character => character == '.') <= 1
            && number[0] != '.' && number[^1] != '.'
            && number.All(character => character is >= '0' and <= '9' or '.');
    }

    private static bool TryTokenize(string line, out List<Token> tokens, out string? problem)
    {
        tokens = [];
        problem = null;
        var position = 0;
        while (position < line.Length)
        {
            if (char.IsWhiteSpace(line[position])) { position++; continue; }
            var quoted = line[position] == '"';
            if (quoted) position++;
            var contentStart = position;
            while (position < line.Length && (quoted ? line[position] != '"' : !char.IsWhiteSpace(line[position])))
            {
                if (line[position] == '\\' || line[position] == '"' || line[position] is '\u201c' or '\u201d')
                {
                    problem = "Gearset macro has unsupported escaping or quotation syntax.";
                    return false;
                }
                position++;
            }
            var length = position - contentStart;
            if (quoted && (position == line.Length || line[position] != '"'))
            {
                problem = "Gearset macro contains an unterminated quoted argument.";
                return false;
            }
            tokens.Add(new Token(line.Substring(contentStart, length), contentStart, length, quoted));
            if (quoted)
            {
                position++;
                if (position < line.Length && !char.IsWhiteSpace(line[position]))
                {
                    problem = "Gearset macro has text attached to a quoted argument.";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool AsciiDigits(string value) => value.Length > 0 && value.All(character => character is >= '0' and <= '9');
    private static bool Is(string value, string expected) => value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static GearsetMacroLineResult Unchanged(string line) => new(line, false, null);
    private static GearsetMacroLineResult Block(string line, string problem) => new(line, false, problem);
    private sealed record Token(string Value, int ContentStart, int ContentLength, bool Quoted);
}
