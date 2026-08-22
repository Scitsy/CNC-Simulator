using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FanucSimulator
{
    // Display row for the MACRO screen's variable grids - Value is null for an unset local variable.
    public class MacroVariableRow
    {
        public string Variable { get; set; } = "";
        public double? Value { get; set; }
    }

    // Custom Macro B support: user variables (#1-33 local, #100-999 common), a handful of read-only
    // system variables (#4001 active motion mode, #5001-#5002 current X/Z - added because a real
    // macro turned out to need "where am I", not a general #1000+ implementation), [...] arithmetic
    // expressions, IF/GOTO/WHILE-DO-END branching with AND/OR-joined compound conditions, and
    // G65/G66/G67 macro calls (single and modal). Indirect addressing (#[expr]) and multiple
    // statements per block are deliberately out of scope - see the plan this was built from for the
    // reasoning. Local variables are scoped per G65/G66 call level and saved/restored across nested
    // calls; M98 subprograms deliberately do NOT get their own scope, matching real FANUC behavior
    // where only custom macro calls create a new local variable frame.
    public partial class LatheSimulator
    {
        private const int MaxMacroCallDepth = 8;

        private readonly Stack<double?[]> _localVarStack = new();
        private readonly double?[] _mainLocals = new double?[34]; // level-0 (main program) locals
        private readonly double?[] _commonVars = new double?[1000]; // flat store for #100-#999
        private int _callDepth = 0;

        // G66/G67 modal macro call state - G66 arms these once; the armed macro then auto-fires
        // (with these same captured args) before every subsequent plain motion block until G67
        // clears it. Deliberately does NOT fire before canned-cycle (G71/G72/G75/G76) setup or
        // trigger blocks - those are complex multi-pass cycles with no clean per-block "hole" to
        // insert a macro call into, and real-world G66 usage is point-to-point patterns (repeated
        // drilling/positioning), not wrapped around a roughing cycle.
        private bool _modalMacroActive = false;
        private int _modalMacroProgram = 0;
        private int _modalMacroRepeat = 1;
        private double?[] _modalMacroArgs = new double?[34];

        private double?[] CurrentLocals => _localVarStack.Count > 0 ? _localVarStack.Peek() : _mainLocals;

        // FANUC Custom Macro B "Argument Specification I" - the standard letter-to-local-variable
        // mapping for G65 call arguments. G, L, N, O, P are reserved for the call syntax itself and
        // never appear here; M is deliberately excluded too (a G65 line practically never needs it as
        // an argument letter, and including it would collide with M-code parsing on the same line).
        private static readonly Dictionary<string, int> MacroArgAddressMap = new()
        {
            ["A"] = 1, ["B"] = 2, ["C"] = 3, ["I"] = 4, ["J"] = 5, ["K"] = 6, ["D"] = 7, ["E"] = 8,
            ["F"] = 9, ["H"] = 11, ["Q"] = 17, ["R"] = 18, ["S"] = 19, ["T"] = 20, ["U"] = 21,
            ["V"] = 22, ["W"] = 23, ["X"] = 24, ["Y"] = 25, ["Z"] = 26,
        };

        private static readonly Regex GotoTargetPattern = new(@"GOTO\s*(\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex IfConditionPattern = new(@"IF\s*\[(.*)\].*?GOTO", RegexOptions.IgnoreCase);
        private static readonly Regex WhileConditionPattern = new(@"WHILE\s*\[(.*)\]\s*DO\s*(\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex EndLabelPattern = new(@"END\s*(\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex AssignmentLinePattern = new(@"^#(\d+)\s*=\s*(.+)$", RegexOptions.IgnoreCase);

        // ---- Variable read/write ----

        // Read-only system variables - deliberately just the handful that came up as a real need
        // (O0011's modal-macro demo had to smuggle the target Z through a common variable
        // specifically because there was no way for a macro to ask "where am I"). Not a general
        // #1000+ system variable implementation - see the class doc comment for the scope line.
        private double? GetSystemVariable(int number) => number switch
        {
            4001 => (int)Modal.Motion, // active motion modal group: G00=0 G01=1 G02=2 G03=3 (MotionMode's own enum order)
            5001 => X,
            5002 => Z,
            _ => null,
        };

        private static bool IsSystemVariable(int number) => number == 4001 || number == 5001 || number == 5002;

        // Null (#0, or any never-assigned variable) reads as "no value" - callers that care about
        // that distinction (EvaluateCondition's EQ/NE-against-null handling) use this directly;
        // everywhere else (arithmetic) an unset variable simply reads as 0 via GetVariableOrZero.
        private double? GetVariable(int number)
        {
            if (number == 0)
                return null;
            if (IsSystemVariable(number))
                return GetSystemVariable(number);
            if (number >= 1 && number <= 33)
                return CurrentLocals[number];
            if (number >= 100 && number <= 999)
                return _commonVars[number];

            Alarms.Add(new Alarm(115, $"Macro: #{number} is outside the supported range (1-33, 100-999, 4001, 5001-5002)"));
            return null;
        }

        private double GetVariableOrZero(int number) => GetVariable(number) ?? 0;

        // Public so the MACRO screen's grids can write a value directly (poking a variable mid-
        // debug without re-running the whole program) through the exact same rules a program's own
        // #n=... assignment goes through - #0/system-variable guards included, even though neither
        // is reachable from those grids today (they only ever list #1-33 and assigned #100-999).
        public void SetVariable(int number, double value)
        {
            if (number == 0)
            {
                Messages.Add("Macro: #0 is always null - assignment ignored");
                return;
            }
            if (IsSystemVariable(number))
            {
                Alarms.Add(new Alarm(115, $"Macro: #{number} is a read-only system variable"));
                return;
            }
            if (number >= 1 && number <= 33)
            {
                CurrentLocals[number] = value;
                Messages.Add($"#{number} = {FormatNumber(value)}");
                return;
            }
            if (number >= 100 && number <= 999)
            {
                _commonVars[number] = value;
                Messages.Add($"#{number} = {FormatNumber(value)}");
                return;
            }

            Alarms.Add(new Alarm(115, $"Macro: #{number} is outside the supported range (1-33, 100-999)"));
        }

        private static string FormatNumber(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        // ---- Variable display (for the MACRO screen) ----

        // All 33 local variables in the currently active scope (the main program, or the innermost
        // active G65 call) - shown whether set or not, matching a real control's variable screen,
        // where the local range is small and fixed enough that blank rows are still useful context.
        public List<MacroVariableRow> GetLocalVariableRows()
        {
            var rows = new List<MacroVariableRow>();
            for (int n = 1; n <= 33; n++)
                rows.Add(new MacroVariableRow { Variable = $"#{n}", Value = CurrentLocals[n] });
            return rows;
        }

        // Only the common variables (#100-#999) that have actually been assigned - the full
        // 900-slot range would be an unusable wall of blank rows in a plain list without the paging
        // a real control's screen has, which this simulator doesn't model.
        public List<MacroVariableRow> GetCommonVariableRows()
        {
            var rows = new List<MacroVariableRow>();
            for (int n = 100; n <= 999; n++)
                if (_commonVars[n].HasValue)
                    rows.Add(new MacroVariableRow { Variable = $"#{n}", Value = _commonVars[n] });
            return rows;
        }

        // ---- Expression evaluation ----
        //
        // A small recursive-descent parser/evaluator over FANUC macro expression syntax. Grouping
        // uses square brackets (round parens are already stripped as comments before any of this
        // code sees a line). Grammar:
        //   expr    := term (('+' | '-') term)*
        //   term    := factor (('*' | '/' | MOD) factor)*
        //   factor  := ('-' | '+') factor | function factor | '[' expr ']' | '#' int | number
        //   function:= SIN | COS | TAN | ATAN | SQRT | ABS | ROUND | FIX | FUP   (trig in degrees)

        private class ExprCursor
        {
            public readonly string Text;
            public int Pos;
            public ExprCursor(string text) { Text = text; Pos = 0; }
            public bool Eof => Pos >= Text.Length;
            public char Peek => Pos < Text.Length ? Text[Pos] : '\0';
            public void SkipWs() { while (!Eof && char.IsWhiteSpace(Peek)) Pos++; }
        }

        private static readonly string[] FunctionNames = { "ATAN", "SIN", "COS", "TAN", "SQRT", "ABS", "ROUND", "FIX", "FUP" };

        private static bool TryMatchWord(ExprCursor c, string word)
        {
            c.SkipWs();
            if (c.Pos + word.Length > c.Text.Length)
                return false;
            if (string.Compare(c.Text, c.Pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            var next = c.Pos + word.Length < c.Text.Length ? c.Text[c.Pos + word.Length] : '\0';
            if (char.IsLetterOrDigit(next))
                return false;
            c.Pos += word.Length;
            return true;
        }

        private static string? TryReadFunctionName(ExprCursor c)
        {
            foreach (var name in FunctionNames)
                if (TryMatchWord(c, name))
                    return name;
            return null;
        }

        private static double ApplyFunction(string name, double arg) => name switch
        {
            "SIN" => Math.Sin(arg * Math.PI / 180.0),
            "COS" => Math.Cos(arg * Math.PI / 180.0),
            "TAN" => Math.Tan(arg * Math.PI / 180.0),
            "ATAN" => Math.Atan(arg) * 180.0 / Math.PI,
            "SQRT" => arg < 0 ? 0 : Math.Sqrt(arg),
            "ABS" => Math.Abs(arg),
            "ROUND" => Math.Round(arg, MidpointRounding.AwayFromZero),
            "FIX" => Math.Truncate(arg),
            "FUP" => arg >= 0 ? Math.Ceiling(arg) : Math.Floor(arg),
            _ => arg,
        };

        private static int ReadInt(ExprCursor c)
        {
            c.SkipWs();
            var start = c.Pos;
            while (!c.Eof && char.IsDigit(c.Peek))
                c.Pos++;
            return start == c.Pos ? 0 : int.Parse(c.Text.Substring(start, c.Pos - start));
        }

        private static double ReadNumber(ExprCursor c)
        {
            c.SkipWs();
            var start = c.Pos;
            if (c.Peek == '+' || c.Peek == '-')
                c.Pos++;
            while (!c.Eof && (char.IsDigit(c.Peek) || c.Peek == '.'))
                c.Pos++;
            if (start == c.Pos)
                return 0;
            return double.TryParse(c.Text.Substring(start, c.Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private double ParseExpr(ExprCursor c)
        {
            var value = ParseTerm(c);
            while (true)
            {
                c.SkipWs();
                if (c.Peek == '+') { c.Pos++; value += ParseTerm(c); }
                else if (c.Peek == '-') { c.Pos++; value -= ParseTerm(c); }
                else break;
            }
            return value;
        }

        private double ParseTerm(ExprCursor c)
        {
            var value = ParseFactor(c);
            while (true)
            {
                c.SkipWs();
                if (c.Peek == '*') { c.Pos++; value *= ParseFactor(c); }
                else if (c.Peek == '/') { c.Pos++; var d = ParseFactor(c); value = d == 0 ? 0 : value / d; }
                else if (TryMatchWord(c, "MOD")) { var d = ParseFactor(c); value = d == 0 ? 0 : value % d; }
                else break;
            }
            return value;
        }

        private double ParseFactor(ExprCursor c)
        {
            c.SkipWs();
            if (c.Peek == '-') { c.Pos++; return -ParseFactor(c); }
            if (c.Peek == '+') { c.Pos++; return ParseFactor(c); }

            if (c.Peek == '[')
            {
                c.Pos++;
                var v = ParseExpr(c);
                c.SkipWs();
                if (c.Peek == ']') c.Pos++;
                else Alarms.Add(new Alarm(116, "Macro: missing ']' in expression"));
                return v;
            }

            if (c.Peek == '#')
            {
                c.Pos++;
                return GetVariableOrZero(ReadInt(c));
            }

            var func = TryReadFunctionName(c);
            if (func != null)
                return ApplyFunction(func, ParseFactor(c));

            return ReadNumber(c);
        }

        // Parses a single value: a literal number, a #variable, or one bracketed expression - the
        // grammar for both a G-code address word's value and a macro assignment's right-hand side
        // (real FANUC requires the whole RHS to be exactly one of those three forms, not a bare
        // unbracketed arithmetic chain).
        private double EvaluateSingleTerm(string text)
        {
            var c = new ExprCursor(text);
            var v = ParseFactor(c);
            c.SkipWs();
            if (!c.Eof)
                Alarms.Add(new Alarm(116, $"Macro: unexpected text after value: '{text}'"));
            return v;
        }

        // Evaluates the full content of a [...] block (condition operands, assignment RHS bracket
        // contents) where top-level +/-/*// are allowed without further nesting.
        private double EvaluateExpression(string text)
        {
            var c = new ExprCursor(text);
            var v = ParseExpr(c);
            c.SkipWs();
            if (!c.Eof)
                Alarms.Add(new Alarm(116, $"Macro: unexpected text after expression: '{text}'"));
            return v;
        }

        private static readonly string[] ComparisonOps = { "EQ", "NE", "GE", "LE", "GT", "LT" };

        // Real FANUC syntax puts each comparison in its own bracket pair, with AND/OR *between*
        // pairs, not inside one: "IF[#1 GT 0] AND[#2 LT 10] GOTO n". IfConditionPattern/
        // WhileConditionPattern greedily capture from the first '[' to the last ']' before
        // GOTO/DO, so a compound condition's captured text still contains the interior "] AND["
        // (or "] OR[") literally - SkipBracketNoise below treats stray '['/']' between comparisons
        // as insignificant, the same way whitespace is. No precedence between AND and OR on a real
        // control either - both evaluate strictly left to right.
        private static void SkipBracketNoise(ExprCursor c)
        {
            c.SkipWs();
            while (c.Peek == '[' || c.Peek == ']')
            {
                c.Pos++;
                c.SkipWs();
            }
        }

        private bool EvaluateSingleComparison(ExprCursor c)
        {
            var left = ParseExpr(c);
            c.SkipWs();

            string? op = null;
            foreach (var candidate in ComparisonOps)
                if (TryMatchWord(c, candidate)) { op = candidate; break; }

            if (op == null)
            {
                Alarms.Add(new Alarm(116, "Macro: expected a comparison (EQ/NE/GT/LT/GE/LE)"));
                return false;
            }

            var right = ParseExpr(c);
            return op switch
            {
                "EQ" => Math.Abs(left - right) < 1e-9,
                "NE" => Math.Abs(left - right) >= 1e-9,
                "GT" => left > right,
                "LT" => left < right,
                "GE" => left >= right,
                "LE" => left <= right,
                _ => false,
            };
        }

        private bool EvaluateCondition(string bracketContent)
        {
            var c = new ExprCursor(bracketContent);
            var result = EvaluateSingleComparison(c);
            SkipBracketNoise(c);

            while (true)
            {
                var isAnd = TryMatchWord(c, "AND");
                var isOr = !isAnd && TryMatchWord(c, "OR");
                if (!isAnd && !isOr)
                    break;

                SkipBracketNoise(c);
                var next = EvaluateSingleComparison(c);
                result = isAnd ? result && next : result || next;
                SkipBracketNoise(c);
            }

            if (!c.Eof)
                Alarms.Add(new Alarm(116, $"Macro: unexpected text after condition: '{bracketContent}'"));

            return result;
        }

        // Replaces every address-word value in a plain motion/G-code line with its evaluated literal
        // number, so the result can be re-tokenized by GCodeParser.ParseLine exactly like any
        // hand-written line - e.g. "G01 X[#1+5] Z#2 F0.1" with #1=10 #2=-20 becomes "G1 X15 Z-20 F0.1".
        private string SubstituteExpressions(string code)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < code.Length)
            {
                var ch = code[i];
                if (char.IsLetter(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                    i++;

                    var j = i;
                    while (j < code.Length && char.IsWhiteSpace(code[j]))
                        j++;

                    if (j < code.Length && (char.IsDigit(code[j]) || code[j] == '+' || code[j] == '-' || code[j] == '.' || code[j] == '#' || code[j] == '['))
                    {
                        var cursor = new ExprCursor(code) { Pos = j };
                        var value = ParseFactor(cursor);
                        sb.Append(FormatNumber(value));
                        i = cursor.Pos;
                    }
                }
                else
                {
                    sb.Append(ch);
                    i++;
                }
            }
            return sb.ToString();
        }

        // ---- Control-flow statement handlers (Assignment/Goto/IfGoto/WhileDo/End/MacroCall) ----

        private void ExecuteAssignment(GCodeParser.Block block)
        {
            var m = AssignmentLinePattern.Match(block.RawCode);
            if (!m.Success)
            {
                Alarms.Add(new Alarm(117, $"Macro: malformed assignment '{block.RawCode}'"));
                return;
            }
            var varNum = int.Parse(m.Groups[1].Value);
            var value = EvaluateSingleTerm(m.Groups[2].Value.Trim());
            SetVariable(varNum, value);
        }

        private int FindSequenceBlock(List<GCodeParser.Block> blocks, int targetN)
        {
            var idx = blocks.FindIndex(b => b.Params.TryGetValue("N", out var n) && (int)n == targetN);
            if (idx < 0)
                Alarms.Add(new Alarm(118, $"Macro: GOTO target N{targetN} not found"));
            return idx;
        }

        // Dispatches every non-None macro control-flow kind. Called from RunBlockRange, which
        // already filtered out MacroKind.None (plain expression-bearing motion lines resolved via
        // SubstituteExpressions instead).
        private void HandleMacroControlFlow(GCodeParser.Block block, List<GCodeParser.Block> blocks, ref int i)
        {
            switch (block.MacroKind)
            {
                case GCodeParser.MacroKind.Assignment:
                    ExecuteAssignment(block);
                    i++;
                    break;

                case GCodeParser.MacroKind.Goto:
                {
                    var m = GotoTargetPattern.Match(block.RawCode);
                    if (!m.Success)
                    {
                        Alarms.Add(new Alarm(118, $"Macro: malformed GOTO '{block.RawCode}'"));
                        i++;
                        break;
                    }
                    var target = FindSequenceBlock(blocks, int.Parse(m.Groups[1].Value));
                    i = target >= 0 ? target : i + 1;
                    break;
                }

                case GCodeParser.MacroKind.IfGoto:
                {
                    var condM = IfConditionPattern.Match(block.RawCode);
                    var gotoM = GotoTargetPattern.Match(block.RawCode);
                    if (!condM.Success || !gotoM.Success)
                    {
                        Alarms.Add(new Alarm(118, $"Macro: malformed IF/GOTO '{block.RawCode}'"));
                        i++;
                        break;
                    }
                    var condition = condM.Groups[1].Value.Trim();
                    var taken = EvaluateCondition(condition);
                    Messages.Add($"IF[{condition}] -> {(taken ? "true, branching" : "false, continuing")}");
                    if (taken)
                    {
                        var target = FindSequenceBlock(blocks, int.Parse(gotoM.Groups[1].Value));
                        i = target >= 0 ? target : i + 1;
                    }
                    else
                    {
                        i++;
                    }
                    break;
                }

                case GCodeParser.MacroKind.WhileDo:
                {
                    var m = WhileConditionPattern.Match(block.RawCode);
                    if (!m.Success)
                    {
                        Alarms.Add(new Alarm(118, $"Macro: malformed WHILE/DO '{block.RawCode}'"));
                        i++;
                        break;
                    }
                    var label = m.Groups[2].Value;
                    var taken = EvaluateCondition(m.Groups[1].Value.Trim());
                    if (taken)
                    {
                        i++;
                        break;
                    }

                    var endIdx = -1;
                    for (int k = i + 1; k < blocks.Count; k++)
                    {
                        if (blocks[k].MacroKind != GCodeParser.MacroKind.End)
                            continue;
                        var endM = EndLabelPattern.Match(blocks[k].RawCode);
                        if (endM.Success && endM.Groups[1].Value == label)
                        { endIdx = k; break; }
                    }
                    if (endIdx < 0)
                    {
                        Alarms.Add(new Alarm(118, $"Macro: no matching END{label} for WHILE at line {block.Line}"));
                        i++;
                        break;
                    }
                    i = endIdx + 1;
                    break;
                }

                case GCodeParser.MacroKind.End:
                {
                    var m = EndLabelPattern.Match(block.RawCode);
                    var label = m.Success ? m.Groups[1].Value : "";
                    var whileIdx = -1;
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (blocks[k].MacroKind != GCodeParser.MacroKind.WhileDo)
                            continue;
                        var whileM = WhileConditionPattern.Match(blocks[k].RawCode);
                        if (whileM.Success && whileM.Groups[2].Value == label)
                        { whileIdx = k; break; }
                    }
                    if (whileIdx < 0)
                    {
                        Alarms.Add(new Alarm(118, $"Macro: no matching WHILE...DO{label} for END at line {block.Line}"));
                        i++;
                        break;
                    }
                    i = whileIdx;
                    break;
                }

                case GCodeParser.MacroKind.MacroCall:
                    ExecuteMacroCall(block, blocks);
                    i++;
                    break;

                case GCodeParser.MacroKind.ModalMacroStart:
                    ExecuteModalMacroStart(block, blocks);
                    i++;
                    break;

                case GCodeParser.MacroKind.ModalMacroCancel:
                    ExecuteModalMacroCancel();
                    i++;
                    break;
            }
        }

        // Resolves a G65/G66 call line's own P/L/argument-letter values (which may themselves
        // contain expressions) against the CALLER's current variables. Shared by G65 (calls
        // immediately) and G66 (stores the binding for ExecuteModalMacroStart to fire repeatedly
        // later) so both bind arguments identically.
        private (int Program, int Repeat, double?[] Args)? ResolveMacroCallArgs(GCodeParser.Block block, string codeName)
        {
            var resolvedText = SubstituteExpressions(block.RawCode);
            var resolved = new GCodeParser().ParseLine(resolvedText, block.Line, genericArgCapture: true);

            if (!resolved.Params.TryGetValue("P", out var target))
            {
                Alarms.Add(new Alarm(78, $"{codeName}: Missing P (macro program number)"));
                return null;
            }

            var repeat = resolved.Params.TryGetValue("L", out var l) ? Math.Max(1, (int)l) : 1;
            var args = new double?[34];
            foreach (var (letter, varNum) in MacroArgAddressMap)
                if (resolved.Params.TryGetValue(letter, out var v))
                    args[varNum] = v;

            return ((int)target, repeat, args);
        }

        // Pushes a fresh local-variable frame per FANUC's Argument Specification I, runs the called
        // program through the same jump-capable interpreter used for the top-level program (so a
        // macro can itself use IF/GOTO/WHILE, or call another macro, up to MaxMacroCallDepth
        // levels), and pops the frame on return. Shared by G65's immediate call and G66's modal,
        // fire-before-every-motion-block call.
        private void InvokeMacroProgram(int program, int repeat, double?[] args, List<GCodeParser.Block> blocks)
        {
            var labelIndex = blocks.FindIndex(b => b.IsLabel && (int)b.Params["O"] == program);
            if (labelIndex < 0)
            {
                Alarms.Add(new Alarm(78, $"Macro O{program} not found"));
                return;
            }

            if (_callDepth + 1 > MaxMacroCallDepth)
            {
                Alarms.Add(new Alarm(119, $"Macro: call nesting too deep (max {MaxMacroCallDepth} levels) calling O{program}"));
                return;
            }

            for (int r = 0; r < repeat; r++)
            {
                _localVarStack.Push((double?[])args.Clone());
                _callDepth++;
                RunBlockRange(blocks, labelIndex + 1, isTopLevel: false);
                _callDepth--;
                _localVarStack.Pop();
            }
        }

        // G65: unlike the old one-level-only M98 subprogram call, runs through the shared
        // InvokeMacroProgram/RunBlockRange machinery so a macro can itself use IF/GOTO/WHILE or
        // call another macro.
        private void ExecuteMacroCall(GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            var resolved = ResolveMacroCallArgs(block, "G65");
            if (resolved == null)
                return;

            Messages.Add($"G65: Calling O{resolved.Value.Program}" + (resolved.Value.Repeat > 1 ? $" x{resolved.Value.Repeat}" : ""));
            InvokeMacroProgram(resolved.Value.Program, resolved.Value.Repeat, resolved.Value.Args, blocks);
        }

        // G66: arms the modal call - the macro doesn't run yet, it fires (via
        // TriggerModalMacroIfArmed in LatheSimulator.cs's RunBlockRange) before every subsequent
        // plain motion block until G67. Validating the target program exists here, rather than only
        // at first fire, avoids alarming once per motion block for a simple typo'd P number.
        private void ExecuteModalMacroStart(GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            var resolved = ResolveMacroCallArgs(block, "G66");
            if (resolved == null)
                return;

            if (blocks.FindIndex(b => b.IsLabel && (int)b.Params["O"] == resolved.Value.Program) < 0)
            {
                Alarms.Add(new Alarm(78, $"G66: Macro O{resolved.Value.Program} not found"));
                return;
            }

            _modalMacroActive = true;
            _modalMacroProgram = resolved.Value.Program;
            _modalMacroRepeat = resolved.Value.Repeat;
            _modalMacroArgs = resolved.Value.Args;
            Messages.Add($"G66: Modal macro call armed - O{_modalMacroProgram} will run before every subsequent motion block until G67");
        }

        private void ExecuteModalMacroCancel()
        {
            if (_modalMacroActive)
                Messages.Add("G67: Modal macro call cancelled");
            _modalMacroActive = false;
        }

        // A block "commands motion" for G66 purposes if it carries any axis address word - the
        // modal macro fires before such a block, then the block's own move executes normally
        // afterward. Called from RunBlockRange just before its plain-motion ExecuteBlock dispatch.
        private static bool HasMotionWord(GCodeParser.Block block) =>
            block.Params.ContainsKey("X") || block.Params.ContainsKey("Z") ||
            block.Params.ContainsKey("Y") || block.Params.ContainsKey("U") || block.Params.ContainsKey("W");

        private void TriggerModalMacroIfArmed(GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            // _callDepth == 0 restricts firing to the top-level call chain only - never inside the
            // modal macro's own body (or any other nested M98/G65 call). Without this, the modal
            // macro's own motion (e.g. "G00 Z#150" positioning to its target before cutting) would
            // immediately re-trigger the same modal call from inside itself, recursing until the
            // call-depth cap aborts it instead of just cutting the one feature it was meant to.
            if (_modalMacroActive && _callDepth == 0 && HasMotionWord(block))
                InvokeMacroProgram(_modalMacroProgram, _modalMacroRepeat, _modalMacroArgs, blocks);
        }

        // M98: unlike G65, does NOT push a new local-variable frame - a called subprogram sees (and
        // can modify) the caller's own locals, matching real FANUC. Still shares the same
        // depth-limited, jump-capable RunBlockRange, so subprograms can now nest properly instead of
        // the old one-level-only implementation.
        private void ExecuteM98(GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            ExecuteBlock(block); // applies any G/M/motion sharing the line; M98 itself is a no-op in ApplyMCode

            if (!block.Params.TryGetValue("P", out var target))
            {
                Alarms.Add(new Alarm(78, "M98: Missing P (subprogram number)"));
                return;
            }

            var repeat = block.Params.TryGetValue("L", out var l) ? Math.Max(1, (int)l) : 1;
            var labelIndex = blocks.FindIndex(b => b.IsLabel && (int)b.Params["O"] == (int)target);
            if (labelIndex < 0)
            {
                Alarms.Add(new Alarm(78, $"M98: Subprogram O{(int)target} not found"));
                return;
            }

            if (_callDepth + 1 > MaxMacroCallDepth)
            {
                Alarms.Add(new Alarm(119, $"Macro: call nesting too deep (max {MaxMacroCallDepth} levels) calling O{(int)target}"));
                return;
            }

            for (int r = 0; r < repeat; r++)
            {
                _callDepth++;
                RunBlockRange(blocks, labelIndex + 1, isTopLevel: false);
                _callDepth--;
            }
        }
    }
}
