using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FanucSimulator
{
    public class GCodeParser
    {
        // Which Custom Macro B construct a HasMacroSyntax block is - see LatheSimulator.Macro.cs for
        // where each kind is actually interpreted. Assignment/Goto/IfGoto/WhileDo/End are pure
        // control flow and carry no Commands/Params from this initial parse (their RawCode is
        // re-derived at execution time, since a WHILE loop revisits the same block with different
        // variable values on each pass - anything computed once at parse time would go stale).
        // ModalMacroStart (G66) and ModalMacroCancel (G67) bookend a modal macro call - G66 behaves
        // like MacroCall for argument-parsing purposes but stores the binding instead of invoking it
        // immediately; G67 just clears that stored state.
        public enum MacroKind { None, Assignment, Goto, IfGoto, WhileDo, End, MacroCall, ModalMacroStart, ModalMacroCancel }

        public class Block
        {
            public int Line { get; set; }
            public string RawCode { get; set; } = "";
            public List<(char Type, int Code)> Commands { get; set; } = new();
            public Dictionary<string, double> Params { get; set; } = new();

            // True for any line containing '#' variables, '[...]' expressions, IF/GOTO/WHILE
            // keywords, or a G65/G66/G67 macro call - anything the plain numeric-address tokenizer
            // below can't handle and that LatheSimulator's macro interpreter needs to resolve instead.
            public bool HasMacroSyntax { get; set; }
            public MacroKind MacroKind { get; set; } = MacroKind.None;

            // A line containing only "O####" defines a subprogram label.
            public bool IsLabel => Commands.Count == 0 && Params.Count == 1 && Params.ContainsKey("O");
        }

        // \bEND\b alone wouldn't match "END1" - digits are word characters too, so there's no
        // boundary between "D" and "1" for \b to find. Match END directly followed by its label
        // digit instead (mirroring EndPattern below).
        private static readonly Regex MacroSyntaxHint = new(@"[#\[]|\b(IF|GOTO|WHILE)\b|\bEND\s*\d", RegexOptions.IgnoreCase);
        private static readonly Regex AssignmentPattern = new(@"^#(\d+)\s*=", RegexOptions.IgnoreCase);
        private static readonly Regex IfGotoPattern = new(@"\bIF\s*\[.*\].*\bGOTO\b", RegexOptions.IgnoreCase);
        private static readonly Regex GotoPattern = new(@"^\s*GOTO\b", RegexOptions.IgnoreCase);
        private static readonly Regex WhileDoPattern = new(@"\bWHILE\s*\[.*\]\s*DO\s*\d", RegexOptions.IgnoreCase);
        private static readonly Regex EndPattern = new(@"^\s*END\s*\d", RegexOptions.IgnoreCase);
        private static readonly Regex MacroCallPattern = new(@"(?<!\d)G0*65(?!\d)", RegexOptions.IgnoreCase);
        private static readonly Regex ModalMacroStartPattern = new(@"(?<!\d)G0*66(?!\d)", RegexOptions.IgnoreCase);
        private static readonly Regex ModalMacroCancelPattern = new(@"(?<!\d)G0*67(?!\d)", RegexOptions.IgnoreCase);
        private static readonly Regex AddressWordPattern = new(@"([A-Z])([+-]?\d+\.?\d*)", RegexOptions.IgnoreCase);

        public List<Block> Parse(string gcode)
        {
            var blocks = new List<Block>();
            var lines = gcode.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("("))
                    continue;

                var code = Regex.Split(trimmed, @"[;(]")[0].Trim();
                if (string.IsNullOrEmpty(code))
                    continue;

                var block = ParseLine(code, i + 1);
                if (block.Commands.Count > 0 || block.Params.Count > 0 || block.HasMacroSyntax)
                    blocks.Add(block);
            }

            return blocks;
        }

        // Tokenizes a single already-comment-stripped line into a Block. Used both for the initial
        // parse pass above and by LatheSimulator's macro interpreter, which calls this a second time
        // on a line after substituting '#'/'[...]' content into plain literal numbers - at that point
        // it behaves exactly like parsing any ordinary hand-written line.
        //
        // genericArgCapture: true for a resolved G65 call line, where every non-reserved letter (not
        // G/M/L/N/O/P) is a raw macro argument value bound to a local variable, not the letter's
        // usual meaning - a "T20" here is macro argument #20, not a tool change.
        public Block ParseLine(string code, int lineNumber, bool genericArgCapture = false)
        {
            var block = new Block { Line = lineNumber, RawCode = code };

            if (!genericArgCapture)
            {
                if (MacroSyntaxHint.IsMatch(code))
                {
                    block.HasMacroSyntax = true;
                    block.MacroKind =
                        AssignmentPattern.IsMatch(code) ? MacroKind.Assignment :
                        IfGotoPattern.IsMatch(code) ? MacroKind.IfGoto :
                        GotoPattern.IsMatch(code) ? MacroKind.Goto :
                        WhileDoPattern.IsMatch(code) ? MacroKind.WhileDo :
                        EndPattern.IsMatch(code) ? MacroKind.End :
                        MacroCallPattern.IsMatch(code) ? MacroKind.MacroCall :
                        ModalMacroStartPattern.IsMatch(code) ? MacroKind.ModalMacroStart :
                        ModalMacroCancelPattern.IsMatch(code) ? MacroKind.ModalMacroCancel :
                        MacroKind.None;

                    // Control-flow kinds and G65/G66/G67 calls are fully re-derived from RawCode by
                    // the macro interpreter at execution time - nothing else to tokenize here. A
                    // plain motion/G-code line with expressions inside it (MacroKind.None) also
                    // returns here unresolved; the interpreter substitutes and re-parses it.
                    return block;
                }

                // A bare "G65/G66 ..." call with no expressions of its own in its arguments still
                // needs generic argument capture below, not the fixed-letter switch (a literal "T20"
                // must still be read as macro argument #20, not a tool change). G67 never carries
                // arguments, but is still flagged here for the same reason G65/G66 are - so
                // LatheSimulator's macro interpreter sees it via MacroKind rather than the plain
                // G-code dispatch path.
                if (MacroCallPattern.IsMatch(code))
                {
                    block.HasMacroSyntax = true;
                    block.MacroKind = MacroKind.MacroCall;
                    return block;
                }
                if (ModalMacroStartPattern.IsMatch(code))
                {
                    block.HasMacroSyntax = true;
                    block.MacroKind = MacroKind.ModalMacroStart;
                    return block;
                }
                if (ModalMacroCancelPattern.IsMatch(code))
                {
                    block.HasMacroSyntax = true;
                    block.MacroKind = MacroKind.ModalMacroCancel;
                    return block;
                }
            }

            var matches = AddressWordPattern.Matches(code);
            foreach (Match match in matches)
            {
                var letter = match.Groups[1].Value.ToUpperInvariant();
                if (!double.TryParse(match.Groups[2].Value, out var value))
                    continue;

                if (letter == "G" || letter == "M")
                {
                    block.Commands.Add((letter[0], (int)value));
                    continue;
                }

                if (genericArgCapture && letter != "L" && letter != "N" && letter != "O" && letter != "P")
                {
                    block.Params[letter] = value;
                    continue;
                }

                switch (letter)
                {
                    case "X":
                    case "Z":
                    case "Y":
                    case "U":
                    case "W":
                        block.Params[letter] = value;
                        break;
                    case "F":
                        block.Params["Feed"] = value;
                        break;
                    case "S":
                        block.Params["Speed"] = value;
                        break;
                    case "T":
                        ParseToolCode(block, value);
                        break;
                    case "P":
                        block.Params["P"] = value;
                        break;
                    case "L":
                        block.Params["L"] = value;
                        break;
                    case "O":
                        block.Params["O"] = value;
                        break;
                    case "I":
                    case "K":
                    case "R":
                    case "Q":
                        block.Params[letter] = value;
                        break;
                    case "N":
                        block.Params["N"] = value;
                        break;
                }
            }

            return block;
        }

        // Fanuc lathe convention: T0101 -> tool 1, offset 1. Bare T1 -> offset defaults to the tool number.
        private static void ParseToolCode(Block block, double rawValue)
        {
            var raw = (int)rawValue;
            if (raw >= 100)
            {
                block.Params["Tool"] = raw / 100;
                block.Params["Offset"] = raw % 100;
            }
            else
            {
                block.Params["Tool"] = raw;
                block.Params["Offset"] = raw;
            }
        }
    }
}
