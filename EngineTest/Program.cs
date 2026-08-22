using FanucSimulator;

int pass = 0, fail = 0;
void Check(string label, bool condition)
{
    if (condition) { pass++; Console.WriteLine($"  PASS: {label}"); }
    else { fail++; Console.WriteLine($"  FAIL: {label}"); }
}

int NearestIndex(StockProfile p, double z)
{
    var t = (z - p.ZStart) / (p.ZEnd - p.ZStart);
    return Math.Clamp((int)Math.Round(t * StockProfile.Resolution), 0, StockProfile.Resolution);
}

// RunProgram stops (Paused=true) at every M00 and clears Messages/Alarms/Warnings on each call -
// same as the real control needing a Cycle Start per stop. This drives it through to the end
// (or a real alarm) the way repeatedly clicking Execute in the UI does, aggregating alarms across
// every segment instead of only keeping the last one.
List<Alarm> RunFull(LatheSimulator sim, List<GCodeParser.Block> blocks, out List<string> allWarnings)
{
    var allAlarms = new List<Alarm>();
    allWarnings = new List<string>();
    int next = 0;
    while (true)
    {
        var result = sim.RunProgram(blocks, next);
        allAlarms.AddRange(sim.Alarms);
        allWarnings.AddRange(sim.Warnings);
        if (result.ProgramEnded || !result.Paused)
            break;
        next = result.NextBlockIndex;
    }
    return allAlarms;
}

// Every rapid segment ("rapid" or "collision" type) in the toolpath must change only X or only Z,
// never both - that's the whole point of the retract-path fix. Skips the very first segment pair,
// which is just the program's own initial approach move (e.g. "G0 X52 Z2" from the simulator's
// startup position) and has nothing to do with the canned-cycle retract logic being verified here.
bool AllRapidsSingleAxis(LatheSimulator sim)
{
    for (int i = 2; i < sim.ToolPath.Count - 1; i += 2)
    {
        var p1 = sim.ToolPath[i];
        var p2 = sim.ToolPath[i + 1];
        if (p1.Type != "rapid" && p1.Type != "collision")
            continue;
        var dx = Math.Abs(p2.X - p1.X);
        var dz = Math.Abs(p2.Z - p1.Z);
        if (dx > 1e-6 && dz > 1e-6)
            return false;
    }
    return true;
}

Console.WriteLine("===== RETRACT-PATH FIX + COLLISION DETECTION VERIFICATION =====\n");

// ---- Part 1/2: retract-path fix, no more diagonal rapids ----

// 1. G71 roughing: every rapid is single-axis (the bug scenario, traced against stress_test.gcode's own numbers)
{
    var program = "G21\nT0101\nG0 X52 Z2\nG71 U2 R1\nG71 P10 Q80 U0.5 W0.1 F0.25\nN10 G00 X40 Z2\nN20 G01 Z-5 F0.15\nN30 X40 Z-25\nN40 G02 X30 Z-35 R10\nN50 G01 Z-50\nN60 X24 Z-50\nN70 Z-65\nN80 X50 Z-65\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[1] G71 roughing: all rapids single-axis, no diagonal shortcuts");
    Check("no alarms", sim.Alarms.Count == 0);
    Check("every rapid segment is single-axis", AllRapidsSingleAxis(sim));
}

// 2. G72 facing: same check
{
    var program = "G21\nT0101\nG0 X70 Z5\nG72 W2 R1\nG72 P10 Q20 U0.3 W0.1 F0.2\nN10 G0 Z0 X70\nN20 G1 X50 F0.15\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[2] G72 facing: all rapids single-axis");
    Check("no alarms", sim.Alarms.Count == 0);
    Check("every rapid segment is single-axis", AllRapidsSingleAxis(sim));
}

// 3. G76 threading: same check
{
    var program = "G21\nT0404\nG0 X24 Z2\nG76 P020060 Q100 R0.05\nG76 X18.4 Z-20 R0 P800 Q300 F1.5\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[3] G76 threading: all rapids single-axis");
    Check("no alarms", sim.Alarms.Count == 0);
    Check("every rapid segment is single-axis", AllRapidsSingleAxis(sim));
}

// 4. G71 still produces the correct final carved shape (finish contour honored) after the rework
{
    var program = "G21\nT0101\nG0 X52 Z2\nG71 U2 R1\nG71 P10 Q80 U0.5 W0.1 F0.25\nN10 G00 X40 Z2\nN20 G01 Z-5 F0.15\nN30 X40 Z-25\nN40 G02 X30 Z-35 R10\nN50 G01 Z-50\nN60 X24 Z-50\nN70 Z-65\nN80 X50 Z-65\nG70 P10 Q80 F0.1\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[4] G71+G70 still produces the correct finished contour");
    Check("no alarms", sim.Alarms.Count == 0);
    Check("finished ~X40 near Z-10", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 40) < 1.0);
    Check("finished ~X24 near Z-55", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -55)] - 24) < 1.0);
}

// ---- Part 3: collision detection ----

// 5. Deliberately-bad program: manual rapid straight into untouched stock warns and tags collision.
// A rapid crash isn't something a real control can know about (no Alarm) - it's caught here purely
// as a simulator convenience, logged as a Warning instead. See the comment at the collision check
// in LatheSimulator.cs's MoveTo for the reasoning.
{
    var program = "G21\nT0101\nG0 X76.2 Z2\nG1 X40 Z2 F0.2\nG1 Z-20 F0.15\nG0 X76.2 Z2\nG0 X10 Z-40\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[5] Manual rapid straight into untouched stock warns and tags collision");
    Check("collision warning fires", sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
    Check("last rapid segment tagged collision", sim.ToolPath[^1].Type == "collision" && sim.ToolPath[^2].Type == "collision");
}

// 6. Rapid that stays outside the stock never alarms
{
    var program = "G21\nT0101\nG0 X76.2 Z2\nG0 Z-90\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[6] Rapid staying outside the stock doesn't alarm");
    Check("no collision warning", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
}

// 7. Rapid landing exactly flush with the surface doesn't alarm
{
    var program = "G21\nT0101\nG0 X76.2 Z2\nG0 X76.2 Z-40\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[7] Rapid flush with the surface doesn't alarm");
    Check("no collision warning", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
}

// 8. Rapid through an already-cleared region doesn't alarm
{
    var program = "G21\nT0101\nG0 X76.2 Z2\nG1 X40 Z2 F0.2\nG1 Z-40 F0.15\nG0 X50 Z2\nG0 X50 Z-30\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[8] Rapid through an already-turned-down (cleared) region doesn't alarm");
    Check("no collision warning", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
}

// 9. Rapid through the hollow interior of an already-bored hole doesn't alarm
{
    var program = "G21\nT0303\nG0 X10 Z2\nG1 X20 Z2 F0.1\nG1 Z-30 F0.1\nG0 X15 Z2\nG0 X15 Z-15\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[9] Rapid through the hollow interior of an already-bored hole doesn't alarm");
    Check("no collision warning", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
}

// 10. The very first rapid of a run is exempt, even aimed straight at solid stock
{
    var program = "G21\nT0101\nG0 X10 Z-40\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[10] Very first rapid of a run is exempt from collision checking");
    Check("no collision warning", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
}

// 11. The first rapid right after a tool change is exempt; the next rapid on the same tool is checked.
// Deliberately single-axis probes throughout so each crossing is unambiguous.
{
    var program = "G21\nT0101\nG0 X76.2 Z2\nG1 X40 Z2 F0.2\nG1 Z-20 F0.15\nG0 X76.2 Z-20\nG0 X76.2 Z2\nT0202\nG0 X10 Z-10\nG0 X76.2 Z-10\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[11] First rapid after a tool change is exempt; the next one on that tool is checked");
    Check("exactly one collision warning (the post-exemption X10->X76.2 crossing, not the exempt approach)",
        sim.Warnings.Count(w => w.Contains("COLLISION WARNING")) == 1);
}

// 12. Feed moves through material never alarm, regardless of depth (they're supposed to cut)
{
    var program = "G21\nT0101\nG0 X76.2 Z2\nG1 X10 Z2 F0.2\nG1 Z-40 F0.15\nM30\n";
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[12] Feed moves through material never alarm");
    Check("no collision warning", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
}

// ---- Critical regression: trusted fixtures must produce zero new collision warnings ----

void RegressionCheck(string label, string path, double stockDiameter, double stockLength)
{
    Console.WriteLine(label);
    if (!File.Exists(path))
    {
        Check("file found", false);
        return;
    }
    var sim = new LatheSimulator();
    // Stock size is a session/UI setting, not something G-code sets itself - match each file's own
    // documented part size instead of leaving the simulator's unrelated default in place, or an
    // oversized default stock can mask (or an undersized one can fabricate) collisions that have
    // nothing to do with the program's own correctness.
    sim.StockDiameter = stockDiameter;
    sim.StockLength = stockLength;
    sim.ResetStockProfile();
    Exception? thrown = null;
    try { sim.RunProgram(new GCodeParser().Parse(File.ReadAllText(path))); }
    catch (Exception ex) { thrown = ex; }
    Check("no exception", thrown == null);
    Check("zero collision warnings (rapid collision)", !sim.Warnings.Any(w => w.Contains("COLLISION WARNING")));
    Check("zero alarm-85 (tool mismatch)", !sim.Alarms.Any(a => a.Number == 85));
    // Single-axis-ness is only asserted on the ENGINE'S OWN generated moves (checks 1-3) - hand-
    // written G-code in these reference files is free to use a diagonal rapid as long as it's
    // actually safe (e.g. retreating to full clearance past the face), which is what the collision
    // warning check above already verifies.
}

RegressionCheck("[13] Regression: sample.gcode (real reference program, 2.5in OD x 3in length per its own comments)",
    @"Fixtures\sample.gcode",
    63.5, 76.2);

RegressionCheck("[14] Regression: stress_test.gcode (comprehensive OD/face/ID/groove/thread, 50mm OD x 80mm length per its own comments)",
    @"Fixtures\stress_test.gcode",
    50, 80);

// ---- New: internal (ID) grooving via the just-added ToolType.IdGrooving ----

// 15. G75 with an IdGrooving tool must widen the bore (CarveInner) at the groove location, leaving
// the OD untouched - the opposite carving direction from external grooving.
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 50;
    sim.StockLength = 80;
    sim.ResetStockProfile();
    sim.Offsets.GetOrCreateTool(6).Type = ToolType.IdGrooving;
    sim.Offsets.GetOrCreateTool(6).Width = 3;
    sim.Offsets.GetOrCreateTool(6).InsertReach = 6;

    // T0303 = default IdBoring tool: bore a pilot hole to X20 (diameter) from Z2 to Z-30.
    // T0606 = the IdGrooving tool just configured: cut a groove widening the bore to X26 at Z-15.
    var program = "G21\nT0303\nG0 X10 Z2\nG1 X20 Z2 F0.1\nG1 Z-30 F0.1\nG0 X10 Z2\nT0606\nG0 X17 Z-15\nG75 X26 Z-15 P500 R0.5 F0.1\nM30\n";
    var sim2 = sim;
    sim2.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[15] G75 internal grooving (ToolType.IdGrooving) widens the bore, not the OD");
    Check("no alarms", sim2.Alarms.Count == 0);
    var idxGroove = NearestIndex(sim2.Stock, -15);
    var idxPlainBore = NearestIndex(sim2.Stock, -25); // untouched by the groove, still the plain bored diameter
    Check("bore widened to ~X26 (diameter) at groove Z-15", Math.Abs(sim2.Stock.InnerX[idxGroove] - 26) < 1.0);
    Check("plain bore elsewhere stays ~X20", Math.Abs(sim2.Stock.InnerX[idxPlainBore] - 20) < 1.0);
    Check("OD untouched (~50) at groove Z", Math.Abs(sim2.Stock.OuterX[idxGroove] - 50) < 1.0);
}

// 16. Sanity: existing external grooving (ToolType.Grooving) still carves the OD as before (no
// regression from the internal/external branch added to ExecuteGroovingCycle).
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 50;
    sim.StockLength = 80;
    sim.ResetStockProfile();
    var program = "G21\nT0505\nG0 X52 Z-15\nG75 X44 Z-15 P500 R0.5 F0.1\nM30\n";
    sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[16] G75 external grooving (ToolType.Grooving) still carves the OD");
    Check("no alarms", sim.Alarms.Count == 0);
    var idx = NearestIndex(sim.Stock, -15);
    Check("OD narrowed to ~X44 at groove Z-15", Math.Abs(sim.Stock.OuterX[idx] - 44) < 1.0);
    Check("bore stays solid (InnerX ~0) at groove Z", sim.Stock.InnerX[idx] < 1.0);
}

// 17. Full geometry check of the new O0008 pipe-fitting program (headless, matching its own
// declared 34mm OD x 55mm length stock).
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 34;
    sim.StockLength = 55;
    sim.ResetStockProfile();
    var path = @"..\NCFiles\O0008_pipe_fitting_npt.nc";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(File.ReadAllText(path)), out var allWarnings);
    Console.WriteLine("[17] O0008 pipe fitting: full geometry check");
    Check("no alarms", allAlarms.Count == 0);
    foreach (var a in allAlarms) Console.WriteLine($"    ALM{a.Number}: {a.Message}");
    foreach (var w in allWarnings) Console.WriteLine($"    WARN: {w}");

    Check("smooth ID bore ~10mm away from the groove (Z-10)", Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, -10)] - 10) < 0.5);
    Check("smooth ID bore ~10mm away from the groove (Z-35)", Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, -35)] - 10) < 0.5);
    Check("ID O-ring groove widens bore to ~13mm at Z-25.75", Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, -25.75)] - 13) < 1.0);

    Check("main body OD ~20.2mm away from fillet/groove (Z-30)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -30)] - 20.2) < 0.5);
    Check("OD O-ring groove narrows OD to ~17mm at Z-17.5", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -17.5)] - 17) < 1.0);

    Check("fillet is monotonic increasing from thread OD to body OD (no dip/spike)",
        sim.Stock.OuterX[NearestIndex(sim.Stock, -10.2)] < sim.Stock.OuterX[NearestIndex(sim.Stock, -11.25)] &&
        sim.Stock.OuterX[NearestIndex(sim.Stock, -11.25)] < sim.Stock.OuterX[NearestIndex(sim.Stock, -12.3)]);
    Check("fillet radius stays within the expected [15.2, 20.2] diameter band, no overshoot",
        sim.Stock.OuterX[NearestIndex(sim.Stock, -11.25)] > 15.2 && sim.Stock.OuterX[NearestIndex(sim.Stock, -11.25)] < 20.2);

    Check("NPT thread minor OD near tip (Z-1) smaller than near base (Z-9) - taper direction correct",
        sim.Stock.OuterX[NearestIndex(sim.Stock, -1)] < sim.Stock.OuterX[NearestIndex(sim.Stock, -9)]);
    Check("NPT thread minor OD roughly in the expected 13-14mm band at Z-5",
        sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] > 12.5 && sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] < 14.5);

    Check("ID bore never exceeds OD anywhere (no inverted/collapsed stock)",
        Enumerable.Range(0, sim.Stock.InnerX.Length).All(i => sim.Stock.InnerX[i] <= sim.Stock.OuterX[i] + 1e-6));

    Check("zero collision warnings (every rapid approach/retract stays clear of remaining stock)", allWarnings.Count == 0);
}

// 17b. Regression: O0007_full_stress_test.nc (M00-bearing, non-macro) still runs clean end to end
// through the new RunBlockRange-based dispatch - the parser/execution refactor must not change
// behavior for a program with zero macro syntax.
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 50;
    sim.StockLength = 80;
    sim.ResetStockProfile();
    var path = @"..\NCFiles\O0007_full_stress_test.nc";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(File.ReadAllText(path)), out var allWarnings);
    Console.WriteLine("[17b] Regression: O0007_full_stress_test.nc (M00 breaks, no macro syntax)");
    Check("no alarms", allAlarms.Count == 0);
    Check("no collision warnings", allWarnings.Count == 0);
}

// ---- New: Custom Macro B (variables, expressions, IF/GOTO/WHILE, G65 calls) ----

// 18. Assignment + expression, then substituted into an ordinary motion line (black-box: check the
// resulting bore diameter, not any internal variable state).
{
    var sim = new LatheSimulator();
    var program = "G21\nT0303\n#1=10\n#2=[#1+5]\nG0 X0 Z2\nG1 X#2 Z0 F0.1\nM30\n";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(program), out var warnings);
    Console.WriteLine("[18] Assignment + expression substituted into a motion line");
    Check("no alarms", allAlarms.Count == 0);
    Check("bore carved to X15 (10+5) at the face", Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, 0)] - 15) < 0.5);
}

// 19. IF/GOTO: branch taken vs not taken must reach genuinely different end states.
{
    var takenProgram = "G21\nT0101\n#1=5\nIF[#1 EQ 5] GOTO 20\nG0 X77 Z2\nGOTO 30\nN20 G0 X50 Z2\nN30 G0 Z5\nM30\n";
    var sim1 = new LatheSimulator();
    var alarms1 = RunFull(sim1, new GCodeParser().Parse(takenProgram), out _);
    Console.WriteLine("[19] IF/GOTO branch taken vs not taken");
    Check("taken: no alarms", alarms1.Count == 0);
    Check("taken: IF true jumped past the X77 line, ended at X50", Math.Abs(sim1.X - 50) < 0.01);

    var notTakenProgram = "G21\nT0101\n#1=3\nIF[#1 EQ 5] GOTO 20\nG0 X77 Z2\nGOTO 30\nN20 G0 X50 Z2\nN30 G0 Z5\nM30\n";
    var sim2 = new LatheSimulator();
    var alarms2 = RunFull(sim2, new GCodeParser().Parse(notTakenProgram), out _);
    Check("not taken: no alarms", alarms2.Count == 0);
    Check("not taken: IF false fell through to X77, then GOTO skipped N20, ended at X77", Math.Abs(sim2.X - 77) < 0.01);
}

// 20. WHILE/DO/END: loop must run exactly the right number of times and terminate.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\n#1=0\nWHILE[#1 LT 5] DO1\n#1=[#1+1]\nEND1\nG0 X#1 Z2\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[20] WHILE/DO/END loop iteration count");
    Check("no alarms", alarms.Count == 0);
    Check("loop counted 0->5 (5 iterations) then stopped, final move to X5", Math.Abs(sim.X - 5) < 0.01);
}

// 21. G65 argument binding + nested G65 (a macro calling another macro) - the exact scenario the
// old one-level-only M98 implementation could never do.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0101\nG65 P9002 A3 B4\nM30\n" +
        "O9002 (CALLS O9001, PROVING NESTED G65)\n#1=[#1*10]\nG65 P9001 A#1 B#2\nM99\n" +
        "O9001 (LEAF MACRO: MOVE TO X=[A+B])\n#3=[#1+#2]\nG0 X#3 Z2\nM99\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[21] G65 argument binding + nested G65 call");
    Check("no alarms", alarms.Count == 0);
    Check("O9002 scaled A(3)*10=30 in its own #1, called O9001 with A=30 B=4, which moved to X=34",
        Math.Abs(sim.X - 34) < 0.01);
}

// 22. Recursion depth limit: a macro calling itself unconditionally must alarm once and unwind
// cleanly instead of crashing or hanging.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG65 P9005\nM30\n" + "O9005 (INFINITE RECURSION FOR DEPTH-LIMIT TEST)\nG65 P9005\nM99\n";
    var result = sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[22] Recursion depth limit");
    Check("program still reaches M30 (doesn't hang/crash)", result.ProgramEnded);
    Check("exactly one depth-limit alarm (119)", sim.Alarms.Count(a => a.Number == 119) == 1);
}

// 23. Full geometry check of O0009 (headless, matching its own declared 30mm OD x 50mm length stock).
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 30;
    sim.StockLength = 50;
    sim.ResetStockProfile();
    var path = @"..\NCFiles\O0009_macro_groove_pattern.nc";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(File.ReadAllText(path)), out var allWarnings);
    Console.WriteLine("[23] O0009 macro groove pattern: full geometry check");
    Check("no alarms", allAlarms.Count == 0);
    foreach (var a in allAlarms) Console.WriteLine($"    ALM{a.Number}: {a.Message}");
    foreach (var w in allWarnings) Console.WriteLine($"    WARN: {w}");

    Check("body OD ~24mm before groove 0 (Z-5)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] - 24) < 0.5);
    Check("body OD ~24mm between groove 0 and 1 (Z-17)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -17)] - 24) < 0.5);
    Check("body OD ~24mm between groove 1 and 2 (Z-27)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -27)] - 24) < 0.5);
    Check("body OD ~24mm after groove 2 (Z-37)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -37)] - 24) < 0.5);

    Check("groove 0 (i=0, Z-10 to Z-13) cut to ~18mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -11.5)] - 18) < 1.0);
    Check("groove 1 (i=1, Z-20 to Z-23) cut to ~18mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -21.5)] - 18) < 1.0);
    Check("groove 2 (i=2, Z-30 to Z-33) cut to ~18mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -31.5)] - 18) < 1.0);
}

// ---- New: AND/OR compound conditions in IF/WHILE ----

// 25. IF[a] AND[b] GOTO - all 4 truth-table combinations, each reaching a genuinely different
// final X so the branch decision is unambiguous (matches test 19's taken/not-taken pattern).
{
    string Program(double a, double b) =>
        $"G21\nT0101\n#1={a}\n#2={b}\nIF[#1 GT 0] AND[#2 LT 10] GOTO 20\nG0 X77 Z2\nGOTO 30\nN20 G0 X50 Z2\nN30 G0 Z5\nM30\n";

    Console.WriteLine("[25] IF[a] AND[b] GOTO - truth table");
    var cases = new (double A, double B, double ExpectedX, string Label)[]
    {
        (5, 3, 50, "true AND true -> taken"),
        (-5, 3, 77, "false AND true -> not taken"),
        (5, 15, 77, "true AND false -> not taken"),
        (-5, 15, 77, "false AND false -> not taken"),
    };
    foreach (var (a, b, expectedX, label) in cases)
    {
        var sim = new LatheSimulator();
        var alarms = RunFull(sim, new GCodeParser().Parse(Program(a, b)), out _);
        Check($"no alarms (A={a} B={b})", alarms.Count == 0);
        Check($"{label} (A={a} B={b}): ended at X{expectedX}", Math.Abs(sim.X - expectedX) < 0.01);
    }
}

// 26. IF[a] OR[b] GOTO - same truth table, OR semantics.
{
    string Program(double a, double b) =>
        $"G21\nT0101\n#1={a}\n#2={b}\nIF[#1 GT 0] OR[#2 LT 10] GOTO 20\nG0 X77 Z2\nGOTO 30\nN20 G0 X50 Z2\nN30 G0 Z5\nM30\n";

    Console.WriteLine("[26] IF[a] OR[b] GOTO - truth table");
    var cases = new (double A, double B, double ExpectedX, string Label)[]
    {
        (5, 3, 50, "true OR true -> taken"),
        (-5, 3, 50, "false OR true -> taken"),
        (5, 15, 50, "true OR false -> taken"),
        (-5, 15, 77, "false OR false -> not taken"),
    };
    foreach (var (a, b, expectedX, label) in cases)
    {
        var sim = new LatheSimulator();
        var alarms = RunFull(sim, new GCodeParser().Parse(Program(a, b)), out _);
        Check($"no alarms (A={a} B={b})", alarms.Count == 0);
        Check($"{label} (A={a} B={b}): ended at X{expectedX}", Math.Abs(sim.X - expectedX) < 0.01);
    }
}

// 27. WHILE[a] AND[b] DO1 - loop must stop as soon as EITHER condition goes false (the tighter of
// the two counters), not just the first one written.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\n#1=0\n#2=0\nWHILE[#1 LT 5] AND[#2 LT 3] DO1\n#1=[#1+1]\n#2=[#2+1]\nEND1\nG0 X#1 Z2\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[27] WHILE[a] AND[b] DO/END - stops at the tighter bound");
    Check("no alarms", alarms.Count == 0);
    Check("stopped after 3 iterations (bounded by #2 LT 3, not #1 LT 5), final X=3", Math.Abs(sim.X - 3) < 0.01);
}

// 28. Backward-compat: a plain single-comparison IF/WHILE (no AND/OR) still works exactly as
// before the change - re-run of test 20's WHILE loop, expressed inline again to be self-contained.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\n#1=0\nWHILE[#1 LT 5] DO1\n#1=[#1+1]\nEND1\nG0 X#1 Z2\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[28] Regression: plain single-comparison WHILE (no AND/OR) unaffected");
    Check("no alarms", alarms.Count == 0);
    Check("loop still counts 0->5 and stops, final X=5", Math.Abs(sim.X - 5) < 0.01);
}

// ---- New: G66/G67 modal macro calls ----

// 29. G66 fires the armed macro before every subsequent motion block, using the args captured at
// arm time, and stops firing once G67 cancels it.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0101\n#1=100\nG66 P9001 A#1\nG0 X10 Z2\nG0 X20 Z-5\nG67\nG0 X30 Z-10\nM30\n" +
        "O9001 (RECORDS A CALL COUNT AND THE LAST-SEEN ARG INTO COMMON VARS)\n#150=[#150+1]\n#151=#1\nM99\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[29] G66 fires before each motion block; G67 stops it");
    Check("no alarms", alarms.Count == 0);
    Check("macro fired exactly twice (the two motion blocks between G66 and G67)", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#150")?.Value ?? -1) - 2) < 0.01);
    Check("captured arg A=100 stayed fixed across both firings", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#151")?.Value ?? -1) - 100) < 0.01);
    Check("all 3 motion blocks still executed normally regardless of firing, final X=30", Math.Abs(sim.X - 30) < 0.01);
}

// 30. G66 deliberately does NOT fire before canned-cycle setup/trigger blocks (documented scope
// limit) - only the plain G00 approach move (which does carry X/Z) should count.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0505\n#150=0\nG66 P9001\nG00 X30 Z-5\nG75 R0.3\nG75 X20 Z-8 P500 Q100 F0.08\nG67\nM30\n" +
        "O9001\n#150=[#150+1]\nM99\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[30] G66 does not fire before G71/G75/G76 setup or trigger blocks");
    Check("no alarms", alarms.Count == 0);
    Check("fired exactly once (only the plain G00 approach, not the G75 setup/trigger)", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#150")?.Value ?? -1) - 1) < 0.01);
}

// 31. G66 with a bad macro number alarms once at arm time, not once per subsequent motion block.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG66 P9999\nG0 X10 Z2\nG0 X20 Z-5\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[31] G66 with a nonexistent macro number");
    Check("exactly one alarm (not one per motion block)", alarms.Count(a => a.Number == 78) == 1);
    Check("motion still proceeds normally, final X=20", Math.Abs(sim.X - 20) < 0.01);
}

// 32. G66 L(repeat) fires the macro that many times per triggering motion block.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0101\n#150=0\nG66 P9001 L3\nG0 X10 Z2\nG67\nM30\n" +
        "O9001\n#150=[#150+1]\nM99\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[32] G66 L(repeat) fires the macro L times per motion block");
    Check("no alarms", alarms.Count == 0);
    Check("fired 3 times for the one motion block", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#150")?.Value ?? -1) - 3) < 0.01);
}

// 33. Full geometry check of O0011 (headless, matching its own declared 30mm OD x 50mm length
// stock) - the actual demonstration program, G66/G67 driving three grooves at the Z's the main
// program feeds through common variable #150.
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 30;
    sim.StockLength = 50;
    sim.ResetStockProfile();
    var path = @"..\NCFiles\O0011_modal_macro_demo.nc";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(File.ReadAllText(path)), out var allWarnings);
    Console.WriteLine("[33] O0011 modal macro demo: full geometry check");
    Check("no alarms", allAlarms.Count == 0);
    foreach (var a in allAlarms) Console.WriteLine($"    ALM{a.Number}: {a.Message}");
    foreach (var w in allWarnings) Console.WriteLine($"    WARN: {w}");

    Check("body OD ~24mm away from the grooves (Z-5)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] - 24) < 0.5);
    Check("body OD ~24mm between groove 1 and 2 (Z-15)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -15)] - 24) < 0.5);
    Check("body OD ~24mm between groove 2 and 3 (Z-25)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -25)] - 24) < 0.5);
    Check("body OD ~24mm after groove 3 (Z-35)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -35)] - 24) < 0.5);

    Check("witness groove 1 at Z-10 cut to ~18mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 18) < 1.0);
    Check("witness groove 2 at Z-20 cut to ~18mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -20)] - 18) < 1.0);
    Check("witness groove 3 at Z-30 cut to ~18mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -30)] - 18) < 1.0);
}

// 24. O0007's own OD groove (Z-42 to Z-44, 2mm wide) had the same ridge bug O0008/O0009 already
// fixed - Q2000 (matching the full groove width) left the middle uncut. Now fixed to Q100; this
// checks the groove is solid across its full width, not just at the two original plunge Z's, plus
// a broader regression pass over the rest of the program's geometry to confirm the one-line change
// didn't disturb anything else.
{
    var sim = new LatheSimulator();
    sim.StockDiameter = 50;
    sim.StockLength = 80;
    sim.ResetStockProfile();
    var path = @"..\NCFiles\O0007_full_stress_test.nc";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(File.ReadAllText(path)), out var allWarnings);
    Console.WriteLine("[24] O0007 groove-ridge fix + full regression");
    Check("no alarms", allAlarms.Count == 0);
    Check("no collision warnings", allWarnings.Count == 0);

    Check("groove solid at Z-42.25 (was the ridge midpoint before the fix)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -42.25)] - 24) < 0.5);
    Check("groove solid at Z-43.0 (groove center)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -43.0)] - 24) < 0.5);
    Check("groove solid at Z-43.75 (was the ridge midpoint before the fix)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -43.75)] - 24) < 0.5);
    Check("groove floor still exactly ~24mm (unchanged target diameter)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -43)] - 24) < 0.2);

    Check("OD rough/finish contour still correct near Z-10 (~40mm)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 40) < 1.0);
    Check("OD rough/finish contour still correct near Z-40, away from the groove (~30mm)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -40)] - 30) < 1.0);
    Check("tapered bore still correct near its Z-30 end (~14mm)", Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, -29)] - 14) < 0.5);
    Check("thread section still cut near Z-60 (OD reduced below the 24mm turned diameter)", sim.Stock.OuterX[NearestIndex(sim.Stock, -60)] < 24 - 0.5);
}

// ---- New: full G/M-code functionality audit (codes with real behavioral consequence but zero
// prior test coverage - found by cross-referencing GCodeReference.cs's documented set against
// every .nc/.gcode fixture and inline test program) ----

// 34. G91 incremental positioning: relative moves must accumulate from the current position, not
// jump to absolute coordinates.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X10 Z0\nG91\nG01 X5 Z-3 F0.1\nG01 X5 Z-3 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[34] G91 incremental positioning accumulates from current position");
    Check("no alarms", alarms.Count == 0);
    Check("X accumulated 10+5+5=20", Math.Abs(sim.X - 20) < 0.01);
    Check("Z accumulated 0-3-3=-6", Math.Abs(sim.Z - (-6)) < 0.01);
}

// 35. G41/G42/G40 cutter nose radius compensation: a substantial real feature (perpendicular-to-
// travel offset + corner mitering, LatheSimulator.cs MoveTo) that had zero test coverage anywhere.
// T1 (CNMG120404) has NoseRadius 0.4mm.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0101\nG00 X50 Z2\nG41\nG01 X30 Z2 F0.1\nG01 Z-10 F0.1\nG40\nG01 Z-11 F0.1\nG01 Z-20 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[35] G41/G42/G40 cutter nose radius compensation");
    Check("no alarms", alarms.Count == 0);
    Check("G41-active section (Z-5) offset OUTWARD by the 0.4mm nose radius: ~X30.4",
        Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] - 30.4) < 0.05);
    Check("G40-cancelled section (Z-15) back to the exact programmed X30 (uncompensated)",
        Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -15)] - 30.0) < 0.05);

    // G42 mirrors G41 - same magnitude, opposite direction.
    var sim2 = new LatheSimulator();
    var program2 = "G21\nT0101\nG00 X50 Z2\nG42\nG01 X30 Z2 F0.1\nG01 Z-10 F0.1\nM30\n";
    RunFull(sim2, new GCodeParser().Parse(program2), out _);
    Check("G42 offset INWARD by the same 0.4mm nose radius: ~X29.6",
        Math.Abs(sim2.Stock.OuterX[NearestIndex(sim2.Stock, -5)] - 29.6) < 0.05);
}

// 36. Work offset G54-G59: Modal.ActiveWorkOffset was tracked but never actually consulted when
// carving/rendering - a real bug (invisible until now since every work offset defaults to X0/Z0,
// a no-op either way). Fixed in LatheSimulator.cs MoveTo to fold the active work offset in
// alongside the tool's own geometry+wear offset, exactly the same established pattern.
{
    var sim = new LatheSimulator(); // default stock: 76.2mm dia x 100mm length
    sim.Offsets.WorkOffsets[55].Z = 10; // G55 origin sits 10mm toward +Z relative to G54
    var program = "G21\nT0101\nG55\nG00 X50 Z-20\nG01 X30 Z-20 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[36] G54-G59 work offset actually shifts the physical carve location");
    Check("no alarms", alarms.Count == 0);
    Check("carved at MACHINE Z-10 (programmed Z-20 + G55's +10 offset): ~X30",
        Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 30) < 0.5);
    Check("nothing carved at the programmed Z-20 itself (still raw ~76.2mm)",
        Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -20)] - 76.2) < 0.5);
}

// 37. G04 dwell: must not crash, misinterpret its P/X value as an axis move, or block subsequent
// motion - both the millisecond (P) and second (X) forms.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X50 Z2\nG04 P500\nG04 X0.5\nG00 X30 Z-5\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[37] G04 dwell (P and X forms) doesn't crash or interfere with motion");
    Check("no alarms", alarms.Count == 0);
    Check("subsequent motion unaffected, final X30 Z-5", Math.Abs(sim.X - 30) < 0.01 && Math.Abs(sim.Z - (-5)) < 0.01);
}

// 38. G28 return to reference position: rapids to work X0/Z0. Bores first so the destination is
// genuinely hollow (a solid bar's centerline is never actually reachable without cutting through
// material first, on a real machine or this one) for a clean, warning-free demonstration.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0303\nG00 X6 Z2\nG01 X20 Z0 F0.1\nG01 Z-30 F0.1\nG00 X6 Z2\nG00 X50 Z50\nG28\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out var warnings);
    Console.WriteLine("[38] G28 return to reference (work X0/Z0)");
    Check("no alarms", alarms.Count == 0);
    Check("no collision warnings (X0 at Z0 is within the just-bored Ø20mm hollow)", warnings.Count == 0);
    Check("final position is X0 Z0", Math.Abs(sim.X) < 0.01 && Math.Abs(sim.Z) < 0.01);
}

// 39. G97 constant RPM holds the programmed speed fixed as diameter changes - contrasted with G96
// constant surface speed, which must recompute RPM as X changes.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG97 S500\nG00 X50 Z2\nG01 X30 Z-10 F0.1\nM30\n";
    RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[39] G97 constant RPM vs G96 constant surface speed");
    Check("G97: RPM stays exactly at the programmed 500 despite the diameter change", Math.Abs(sim.SpindleSpeed - 500) < 0.01);

    var sim2 = new LatheSimulator();
    var program2 = "G21\nT0101\nG96 S150\nG00 X100 Z2\nM03\nG01 X50 Z-5 F0.1\nM30\n";
    RunFull(sim2, new GCodeParser().Parse(program2), out _);
    var expectedRpmAtX50 = 150 * 1000 / (Math.PI * 50);
    Check($"G96: RPM recomputed for the smaller X50 diameter (expect ~{expectedRpmAtX50:F0})",
        Math.Abs(sim2.SpindleSpeed - expectedRpmAtX50) < 1.0);
}

// 40. G20 inch mode: X/Z/F values must be converted to mm internally.
{
    var sim = new LatheSimulator();
    var program = "G20\nT0101\nG00 X2 Z2\nG01 X1 Z-1 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[40] G20 inch mode converts X/Z to mm");
    Check("no alarms", alarms.Count == 0);
    Check("X1 inch -> 25.4mm", Math.Abs(sim.X - 25.4) < 0.01);
    Check("Z-1 inch -> -25.4mm", Math.Abs(sim.Z - (-25.4)) < 0.01);
}

// 41. M01 (optional stop, documented as "not modeled - always continues") and M02 (documented as
// "not separately simulated, see M30") - both intentionally near-inert, confirm they're actually
// harmless (no crash, no alarm, no unexpected pause/stop) rather than untested-and-hoping.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nM01\nG00 X50 Z2\nG01 X30 Z-10 F0.1\nM02\nM30\n";
    var result = sim.RunProgram(new GCodeParser().Parse(program));
    Console.WriteLine("[41] M01 optional stop / M02 alternate program end - both intentionally inert");
    Check("no alarms", sim.Alarms.Count == 0);
    Check("M01 did not pause execution (optional-stop switch not modeled)", !result.Paused || result.ProgramEnded);
    Check("program still reaches M30 and ends normally", result.ProgramEnded);
    Check("motion around M01/M02 executed normally, final X30 Z-10", Math.Abs(sim.X - 30) < 0.01 && Math.Abs(sim.Z - (-10)) < 0.01);
}

// 42. M06 tool change confirmation - a real T-word already selects the tool by itself (established
// FANUC lathe convention); M06 alongside it should be harmless and not double-apply or conflict.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nM06\nG00 X50 Z2\nG01 X30 Z-10 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[42] M06 tool change confirmation alongside a T-word");
    Check("no alarms", alarms.Count == 0);
    Check("tool 1 correctly active", sim.CurrentTool == 1);
    Check("motion proceeds normally, final X30 Z-10", Math.Abs(sim.X - 30) < 0.01 && Math.Abs(sim.Z - (-10)) < 0.01);
}

// 43. #5001/#5002 read the current work-coordinate X/Z position - the gap O0011's modal-macro
// demo had to work around by smuggling Z through a common variable instead.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X30 Z-10\n#101=#5001\n#102=#5002\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[43] #5001/#5002 read current X/Z position");
    Check("no alarms", alarms.Count == 0);
    Check("#5001 captured X30", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#101")?.Value ?? -1) - 30) < 0.01);
    Check("#5002 captured Z-10", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#102")?.Value ?? 1) - (-10)) < 0.01);
}

// 44. #4001 reads the active motion modal group (0/1/2/3 for G00/G01/G02/G03).
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\n" +
        "G00 X30 Z2\n#110=#4001\n" +
        "G01 X30 Z-5 F0.1\n#111=#4001\n" +
        "G02 X30 Z-10 R5 F0.1\n#112=#4001\n" +
        "G01 X30 Z-15 F0.1\n" +
        "G03 X30 Z-20 R5 F0.1\n#113=#4001\n" +
        "M30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[44] #4001 reads active motion modal group");
    Check("no alarms", alarms.Count == 0);
    Check("#4001 == 0 after G00", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#110")?.Value ?? -1) - 0) < 0.01);
    Check("#4001 == 1 after G01", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#111")?.Value ?? -1) - 1) < 0.01);
    Check("#4001 == 2 after G02", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#112")?.Value ?? -1) - 2) < 0.01);
    Check("#4001 == 3 after G03", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#113")?.Value ?? -1) - 3) < 0.01);
}

// 45. Writing to any system variable is rejected with alarm 115 (read-only), not silently accepted.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\n#5001=1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[45] Writing to a system variable raises alarm 115");
    Check("alarm 115 raised", alarms.Exists(a => a.Number == 115));
}

// 46. G74 peck drilling: bores a straight hole to the drill tool's fixed diameter, retracts fully
// to the start Z when done (the R-plane), and cuts no collision warnings along the way (each peck
// clears back by the retract amount before the next one, mirroring G75's chip-clearing pattern).
{
    var sim = new LatheSimulator();
    sim.Offsets.GetOrCreateTool(4).Type = ToolType.Drill;
    sim.Offsets.GetOrCreateTool(4).Width = 10;
    var program = "G21\nT0404\nG00 X0 Z2\nG74 R0.5\nG74 X0 Z-30 Q5000 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out var warnings);
    Console.WriteLine("[46] G74 peck drilling cycle bores a straight hole");
    Check("no alarms", alarms.Count == 0);
    Check("no collision warnings (each peck retracts before the next)", warnings.Count == 0);
    Check("retracted fully back to the start Z2 when the cycle finished", Math.Abs(sim.Z - 2) < 0.01 && Math.Abs(sim.X - 0) < 0.01);
    var idxMidBore = NearestIndex(sim.Stock, -15);
    var idxNearBottom = NearestIndex(sim.Stock, -29);
    Check("bore carved to the drill's 10mm fixed width mid-hole", Math.Abs(sim.Stock.InnerX[idxMidBore] - 10) < 1.0);
    Check("bore carved to the drill's 10mm fixed width near the bottom", Math.Abs(sim.Stock.InnerX[idxNearBottom] - 10) < 1.0);

    // With a 5mm peck over a 32mm total travel (Z2 -> Z-30), the cycle should retract-and-reapproach
    // (rapid) several times, not plunge in one continuous feed move.
    var rapidSegmentsDuringCycle = 0;
    foreach (var seg in sim.ToolPath)
        if (seg.Type == "rapid" && seg.Z < 2 && seg.Z > -30)
            rapidSegmentsDuringCycle++;
    Check("multiple peck-clearing rapid retracts occurred (not a single continuous plunge)", rapidSegmentsDuringCycle >= 4);
}

// 47. G74 with the wrong tool type (no drill selected) is rejected with alarm 85, same fool-proofing
// pattern already established for G71/G72/G75/G76 - and no motion/carving happens.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X0 Z2\nG74 R0.5\nG74 X0 Z-30 Q5000 F0.1\nM30\n"; // T0101 = default OdTurning tool
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[47] G74 with wrong tool type raises alarm 85");
    Check("alarm 85 raised", alarms.Exists(a => a.Number == 85));
    Check("no bore carved (cycle rejected before any motion)", sim.Stock.InnerX[NearestIndex(sim.Stock, -15)] < 0.01);
}

// 48. Full geometry + variable check of O0013 (headless) - the actual demonstration program for
// both Saturday PM features together: G74 peck-drills a centerline hole, then an OD turning pass
// captures #4001 after each motion mode and #5001/#5002 after the drilling cycle retracts.
{
    var sim = new LatheSimulator();
    sim.Offsets.GetOrCreateTool(2).Type = ToolType.Drill;
    sim.Offsets.GetOrCreateTool(2).Width = 8;
    sim.Offsets.GetOrCreateTool(1).Type = ToolType.OdTurning;
    var path = @"..\NCFiles\O0013_g74_and_system_vars_demo.nc";
    var allAlarms = RunFull(sim, new GCodeParser().Parse(File.ReadAllText(path)), out var allWarnings);
    Console.WriteLine("[48] O0013 G74 + system variables demo: full geometry check");
    Check("no alarms", allAlarms.Count == 0);
    foreach (var a in allAlarms) Console.WriteLine($"    ALM{a.Number}: {a.Message}");
    foreach (var w in allWarnings) Console.WriteLine($"    WARN: {w}");
    Check("no collision warnings", allWarnings.Count == 0);

    Check("drilled bore reaches the 8mm target diameter near the bottom (Z-38)", Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, -38)] - 8) < 1.0);
    Check("#5001 captured the drilling cycle's retracted X0", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#101")?.Value ?? -1) - 0) < 0.01);
    Check("#5002 captured the drilling cycle's retracted Z2", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#102")?.Value ?? -1) - 2) < 0.01);

    Check("#4001 == 0 after the G00 approach", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#110")?.Value ?? -1) - 0) < 0.01);
    Check("#4001 == 1 after the G01 step-down", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#111")?.Value ?? -1) - 1) < 0.01);
    Check("#4001 == 2 after the G02 fillet", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#112")?.Value ?? -1) - 2) < 0.01);
    Check("#4001 == 3 after the G03 fillet", Math.Abs((sim.GetCommonVariableRows().Find(r => r.Variable == "#113")?.Value ?? -1) - 3) < 0.01);

    Check("OD profile: step-down diameter ~30mm at Z-15", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -15)] - 30) < 0.5);
    Check("OD profile: convex fillet returns to ~40mm at Z-25", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -25)] - 40) < 0.5);
    Check("OD profile: concave fillet back to ~30mm at Z-50", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -50)] - 30) < 0.5);
}

// 49. Custom tool catalog entries round-trip through SaveCustomEntries/LoadCustomEntries - the
// persistence added for the Tool Builder's "add to tool catalog" checkbox. Uses a temp file and
// restores ToolCatalog.Custom to empty afterward, since it's static/shared process-wide state.
{
    var builtInCountBefore = ToolCatalog.BuiltIn.Count;
    var tempPath = Path.Combine(Path.GetTempPath(), $"fanuc_custom_tools_test_{Guid.NewGuid():N}.json");
    ToolCatalog.Custom.Clear();
    ToolCatalog.Custom.Add(new CatalogEntry
    {
        InsertDesignation = "TEST01", HolderDesignation = "TEST-HOLDER", Description = "Round-trip test entry",
        Type = ToolType.Grooving, Insert = InsertShape.None, NoseRadius = 0.2, Width = 2.5, ShankSize = 20, Overhang = 60
    });

    try
    {
        ToolCatalog.SaveCustomEntries(tempPath);
        ToolCatalog.Custom.Clear();
        Console.WriteLine("[49] Custom tool catalog entries persist across save/load");
        Check("Custom is empty right after Clear (sanity)", ToolCatalog.Custom.Count == 0);

        ToolCatalog.LoadCustomEntries(tempPath);
        Check("exactly one custom entry loaded back", ToolCatalog.Custom.Count == 1);
        Check("field values round-tripped intact", ToolCatalog.Custom.Count == 1 &&
            ToolCatalog.Custom[0].InsertDesignation == "TEST01" &&
            ToolCatalog.Custom[0].Type == ToolType.Grooving &&
            Math.Abs(ToolCatalog.Custom[0].Width - 2.5) < 0.001);
        Check("Entries combines BuiltIn + Custom in that order",
            ToolCatalog.Entries.Count == builtInCountBefore + 1 &&
            ToolCatalog.Entries[builtInCountBefore].InsertDesignation == "TEST01");

        ToolCatalog.LoadCustomEntries(Path.Combine(Path.GetTempPath(), $"fanuc_nonexistent_{Guid.NewGuid():N}.json"));
        Check("loading a missing file leaves Custom untouched (still the 1 entry from above)", ToolCatalog.Custom.Count == 1);
    }
    finally
    {
        ToolCatalog.Custom.Clear(); // restore shared static state for any test that runs after this one
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}

// ---- Realistic cycle-time simulation (SimulatedSecondsElapsed) ----
// Closed-form physics, so these assert exact expected values (tight tolerance for float rounding
// only), not just "some plausible-looking number."

// 50. G01 feed move under G98 (per-minute) - straightforward distance/feedrate.
{
    var sim = new LatheSimulator();
    var program = "G21\nG98\nT0101\nG01 Z-52 F100\nM30\n"; // 52mm @ 100mm/min = 31.2s
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[50] G01 under G98 (per-minute feed): exact expected seconds");
    Check("no alarms", alarms.Count == 0);
    Check("31.2s (52mm @ 100mm/min)", Math.Abs(sim.SimulatedSecondsElapsed - 31.2) < 0.001);
}

// 51. G01 feed move under G99 (per-revolution, the default) - effective mm/min = feed(mm/rev) * RPM.
{
    var sim = new LatheSimulator();
    var program = "G21\nG99\nT0101\nM03 S1000\nG01 Z-52 F0.2\nM30\n"; // 52mm @ (0.2*1000)mm/min = 15.6s
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[51] G01 under G99 (per-rev feed + RPM): exact expected seconds");
    Check("no alarms", alarms.Count == 0);
    Check("15.6s (52mm @ 200mm/min effective)", Math.Abs(sim.SimulatedSecondsElapsed - 15.6) < 0.001);
}

// 52. G00 rapid move - against the invented 10000mm/min rapid traverse rate.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 Z-50\nM30\n"; // 50mm @ 10000mm/min = 0.3s
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[52] G00 rapid: exact expected seconds against the 10000mm/min default");
    Check("no alarms", alarms.Count == 0);
    Check("0.3s (50mm @ 10000mm/min)", Math.Abs(sim.SimulatedSecondsElapsed - 0.3) < 0.001);
}

// 53. G04 dwell (both P and X forms) now actually costs simulated time, not just a log message.
{
    var sim = new LatheSimulator();
    var alarmsP = RunFull(sim, new GCodeParser().Parse("G21\nT0101\nG04 P500\nM30\n"), out _);
    Console.WriteLine("[53] G04 dwell adds real simulated time (P=ms, X=sec forms)");
    Check("no alarms (P form)", alarmsP.Count == 0);
    Check("G04 P500 -> exactly 0.5s", Math.Abs(sim.SimulatedSecondsElapsed - 0.5) < 0.001);

    var sim2 = new LatheSimulator();
    var alarmsX = RunFull(sim2, new GCodeParser().Parse("G21\nT0101\nG04 X2\nM30\n"), out _);
    Check("no alarms (X form)", alarmsX.Count == 0);
    Check("G04 X2 -> exactly 2.0s", Math.Abs(sim2.SimulatedSecondsElapsed - 2.0) < 0.001);
}

// 54. G02/G03 arc time comes from true arc length (radius * sweep), not the tessellated chords'
// summed straight-line distance - close, but not exact, so this specifically catches a regression
// back to chord-summed distance.
{
    var sim = new LatheSimulator();
    // G00 X20 Z0: rapid 20mm @ 10000mm/min = 0.12s.
    // G02 X30 Z-10 I0 K-10: quarter circle, center (20,-10), radius 10, sweep 90deg (pi/2 rad) ->
    // arc length 10*pi/2 = 15.70796...mm @ 100mm/min (G98) = 9.42478...s.
    var program = "G21\nG98\nT0101\nG00 X20 Z0\nG02 X30 Z-10 I0 K-10 F100\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    var expected = 20.0 / 10000 * 60 + (10.0 * Math.PI / 2) / 100 * 60;
    Console.WriteLine("[54] G02 arc time from true arc length (radius * sweep), not chord distance");
    Check("no alarms", alarms.Count == 0);
    Check($"{expected:F5}s (rapid approach + true arc length @ 100mm/min)",
        Math.Abs(sim.SimulatedSecondsElapsed - expected) < 0.001);
}

// 55. A block's own F-word applies to that same block's move for timing purposes, not the previous
// modal feed - regression check for the ApplyMotion/ApplyArcMotion ordering fix found while
// building this feature (FeedRate used to get assigned *after* the move it was meant to govern).
{
    var sim = new LatheSimulator();
    // G01 Z-10 F50: 10mm @ 50mm/min = 12s. G01 Z-20 F200: 10mm @ the NEW 200mm/min = 3s (would be
    // 12s again, for a buggy total of 24s, if the old feed still governed this move).
    var program = "G21\nG98\nT0101\nG01 Z-10 F50\nG01 Z-20 F200\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[55] A block's own F-word governs that block's own move (ordering fix)");
    Check("no alarms", alarms.Count == 0);
    Check("15s total (12s @ F50 + 3s @ F200, not 24s if the old feed leaked into the second move)",
        Math.Abs(sim.SimulatedSecondsElapsed - 15.0) < 0.001);
}

Console.WriteLine();
Console.WriteLine($"===== TOTAL: {pass} passed, {fail} failed =====");
Environment.Exit(fail == 0 ? 0 : 1);
