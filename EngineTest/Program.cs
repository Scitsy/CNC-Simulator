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

// Runs a program to the end like RunFull, checking after every chunk that the playback timeline
// the engine recorded is faithful to what the engine actually did. Returns the number of chunks, or
// -1 on the first broken invariant (with the reason printed), so each caller can Check() it.
static bool SameRa(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || a == b;

int CheckTimelinesThroughout(LatheSimulator sim, List<GCodeParser.Block> blocks, string label)
{
    int next = 0, chunks = 0;
    while (chunks < 200)
    {
        var result = sim.RunProgram(blocks, next);
        chunks++;
        var tl = sim.LastTimeline;
        string? problem = null;

        if (tl == null)
            problem = "no timeline recorded";
        else
        {
            // 1. The timeline's clock agrees with the cycle-time clock.
            if (Math.Abs(tl.Duration - sim.SimulatedSecondsElapsed) > 1e-9)
                problem = $"duration {tl.Duration} != SimulatedSecondsElapsed {sim.SimulatedSecondsElapsed}";

            // 2. Events are back to back - no gaps, no overlaps - and every one has a real line.
            var t = 0.0;
            foreach (var ev in tl.Events)
            {
                if (problem != null) break;
                if (Math.Abs(ev.StartTime - t) > 1e-9) problem = $"gap/overlap at t={t} (event starts {ev.StartTime})";
                else if (ev.Duration < 0) problem = "negative duration";
                else if (ev.Line <= 0) problem = "event with no program line";
                t = ev.EndTime;
            }
            if (problem == null && Math.Abs(t - tl.Duration) > 1e-9)
                problem = $"events end at {t}, timeline says {tl.Duration}";

            // 3. Markers are in time order and the run ends with the closing marker.
            for (int i = 1; problem == null && i < tl.Markers.Count; i++)
                if (tl.Markers[i].Time < tl.Markers[i - 1].Time - 1e-12)
                    problem = "markers out of time order";
            if (problem == null && (tl.Markers.Count == 0 || tl.Markers[^1].Line != 0))
                problem = "missing closing marker";

            // 4. Replaying every carve onto the start snapshot reproduces the engine's stock exactly.
            var cursor = new PlaybackCursor(tl);
            cursor.Seek(tl.Duration);
            for (int i = 0; problem == null && i <= StockProfile.Resolution; i++)
                if (cursor.Stock.OuterX[i] != sim.Stock.OuterX[i] || cursor.Stock.InnerX[i] != sim.Stock.InnerX[i])
                    problem = $"replayed stock differs from engine stock at sample {i}";
            for (int i = 0; problem == null && i <= StockProfile.Resolution; i++)
                if (!SameRa(cursor.Stock.OuterRa[i], sim.Stock.OuterRa[i]) || !SameRa(cursor.Stock.InnerRa[i], sim.Stock.InnerRa[i]))
                    problem = $"replayed finish differs from engine finish at sample {i}";

            // 5. At the end the tool is where the engine left it, and the whole log is revealed.
            if (problem == null && tl.Events.Count > 0 && cursor.ToolProgrammed is { } end &&
                (Math.Abs(end.X - sim.X) > 1e-9 || Math.Abs(end.Z - sim.Z) > 1e-9))
                problem = $"cursor ends at X{end.X} Z{end.Z}, engine at X{sim.X} Z{sim.Z}";
            if (problem == null && cursor.RevealedLog.Messages != sim.Messages.Count)
                problem = $"end reveals {cursor.RevealedLog.Messages} of {sim.Messages.Count} messages";

            // 6. Seeking back to the start restores the snapshot.
            cursor.Seek(0);
            for (int i = 0; problem == null && i <= StockProfile.Resolution; i++)
                if (cursor.Stock.OuterX[i] != tl.StartStock.OuterX[i] || cursor.Stock.InnerX[i] != tl.StartStock.InnerX[i])
                    problem = $"seek back to 0 leaves stock changed at sample {i}";
        }

        if (problem != null)
        {
            Console.WriteLine($"    {label}, chunk {chunks}: {problem}");
            return -1;
        }

        if (result.ProgramEnded || !result.Paused)
            break;
        next = result.NextBlockIndex;
    }
    return chunks;
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
    // T3 is a boring bar with a 0.4 nose: its round nose, centred 0.4 back from the X15 tip on both
    // axes, only just reaches the face as the move ends there - X15 - 2 x 0.4 = X14.2.
    Check("bore carved to X14.2 at the face (X15 from the macro, less the nose's rounding)",
        Math.Abs(sim.Stock.InnerX[NearestIndex(sim.Stock, 0)] - 14.2) < 1e-6);
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

// 34. Incremental positioning via U/W. G-code system A has no G90/G91 modal pair - incremental is
// expressed by using U/W instead of X/Z, so these moves must accumulate from the current position.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X10 Z0\nG01 U5 W-3 F0.1\nG01 U5 W-3 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[34] U/W incremental positioning accumulates from current position");
    Check("no alarms", alarms.Count == 0);
    Check("X accumulated 10+5+5=20", Math.Abs(sim.X - 20) < 0.01);
    Check("Z accumulated 0-3-3=-6", Math.Abs(sim.Z - (-6)) < 0.01);

    // X/Z stay absolute regardless of what came before - no modal can make them incremental.
    var sim2 = new LatheSimulator();
    RunFull(sim2, new GCodeParser().Parse("G21\nT0101\nG00 X10 Z0\nG01 U5 W-3 F0.1\nG01 X8 Z-1 F0.1\nM30\n"), out _);
    Check("a later X/Z block is absolute, not incremental", Math.Abs(sim2.X - 8) < 0.01 && Math.Abs(sim2.Z - (-1)) < 0.01);
}

// 35. G41/G42/G40 cutter nose radius compensation. T1 (CNMG120404) has NoseRadius 0.4mm and is an
// OD tool (tip direction 3). Turning an OD toward the chuck, G42 is the right side: it puts the round
// nose on the contour. G41 is the wrong side for an OD - it gouges by the nose's whole diameter.
{
    var sim = new LatheSimulator();
    var program =
        "G21\nT0101\nG00 X50 Z2\nG42\nG01 X30 Z2 F0.1\nG01 Z-10 F0.1\nG40\nG01 Z-11 F0.1\nG01 Z-20 F0.1\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[35] G41/G42/G40 cutter nose radius compensation");
    Check("no alarms", alarms.Count == 0);
    Check("G42 (the OD side) cuts exactly on the programmed X30",
        Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] - 30.0) < 1e-6);
    // A straight turn doesn't need comp: the nose's lowest point is level with the imaginary tip.
    Check("G40-cancelled section (Z-15) is also X30 - straight turning is exact without comp",
        Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -15)] - 30.0) < 1e-6);

    var sim2 = new LatheSimulator();
    var program2 = "G21\nT0101\nG00 X50 Z2\nG41\nG01 X30 Z2 F0.1\nG01 Z-10 F0.1\nM30\n";
    RunFull(sim2, new GCodeParser().Parse(program2), out _);
    Check("G41 on an OD (the wrong side) gouges by the nose diameter: X30 - 4 x 0.4 = X28.4",
        Math.Abs(sim2.Stock.OuterX[NearestIndex(sim2.Stock, -5)] - 28.4) < 1e-6);
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

// 38. G28 parks the turret at the reference position, which is clear of the work at the positive
// extreme of both axes - NOT at work X0 Z0, which on a lathe is the spindle centreline at the face
// and would drive the turret straight through the part.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X76.2 Z2\nG01 X60 Z-10 F0.2\nG00 X76.2 Z2\nG28\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out var warnings);
    Console.WriteLine("[38] G28 parks at the reference position, clear of the work");
    Check("no alarms", alarms.Count == 0);
    Check("retracting to reference over solid stock raises no collision warning", warnings.Count == 0);
    Check("final position is the reference position",
        Math.Abs(sim.X - LatheSimulator.ReferenceX) < 0.01 && Math.Abs(sim.Z - LatheSimulator.ReferenceZ) < 0.01);
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

    Check("OD profile: step-down diameter ~30mm at Z-10", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 30) < 0.5);
    // Inside each R5 arc, the shape tells concave from convex (the two differ by 6-7mm here, well
    // beyond the 0.5 allowed: carving can overcut a steep chord by up to one stock sample's spacing).
    // Each is compared at the stock sample
    // nearest the named Z, using that sample's own Z (samples don't land on round numbers).
    // G02 fillet, center X40 (radius 20) Z-15: near Z-18 the radius is 20 - sqrt(25 - dz^2), about
    // X32 (the convex arc would be about X39).
    var iFillet = NearestIndex(sim.Stock, -18);
    var filletDz = sim.Stock.SampleZ(iFillet) - (-15);
    var filletX = 2 * (20 - Math.Sqrt(25 - filletDz * filletDz));
    Check($"OD profile: G02 is the concave fillet (X{filletX:F2} at Z{sim.Stock.SampleZ(iFillet):F2})",
        Math.Abs(sim.Stock.OuterX[iFillet] - filletX) < 0.5);
    Check("OD profile: shoulder diameter ~44mm at Z-28", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -28)] - 44) < 0.5);
    // G03 rounded edge, center X34 (radius 17) Z-35: near Z-38 the radius is 17 + sqrt(25 - dz^2),
    // about X42 (the concave arc would be about X36).
    var iEdge = NearestIndex(sim.Stock, -38);
    var edgeDz = sim.Stock.SampleZ(iEdge) - (-35);
    var edgeX = 2 * (17 + Math.Sqrt(25 - edgeDz * edgeDz));
    Check($"OD profile: G03 is the convex rounded edge (X{edgeX:F2} at Z{sim.Stock.SampleZ(iEdge):F2})",
        Math.Abs(sim.Stock.OuterX[iEdge] - edgeX) < 0.5);
    Check("OD profile: back to ~30mm at Z-50", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -50)] - 30) < 0.5);
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
    // With SAR on (the default) the control first waits for the spindle to ramp 0 -> 1000 RPM at
    // 4500 RPM per 3s (1500 RPM/s): 0.667s, then the cut at full speed.
    var sarWait = 1000.0 / (MachineSpec.MaxSpindleRpm / MachineSpec.SpindleRampSecondsTypical);
    Check($"{15.6 + sarWait:F4}s (SAR wait {sarWait:F4}s + 52mm @ 200mm/min effective)",
        Math.Abs(sim.SimulatedSecondsElapsed - (15.6 + sarWait)) < 0.001);
}

// 52. G00 rapid move - against this machine's real 500 in/min rapid traverse. Programmed in inch so
// the expected value is a clean closed form: 5 in / 500 in/min = 0.01 min = 0.6 s.
{
    var sim = new LatheSimulator();
    var program = "G20\nT0101\nG00 Z-5\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[52] G00 rapid: exact expected seconds at the machine's 500 in/min rapid");
    Check("no alarms", alarms.Count == 0);
    Check("0.6s (5in @ 500in/min)", Math.Abs(sim.SimulatedSecondsElapsed - 0.6) < 0.001);
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
    // G00 X20 Z0 from X0: X20 is a diameter, so the tool travels 10mm @ 12700mm/min (500 in/min).
    // G03 X40 Z-10 I0 K-10: quarter circle - in radius terms from (10,0) about center (10,-10) to
    // (20,-10), radius 10, sweep 90deg -> arc length 10*pi/2 = 15.70796...mm @ 100mm/min (G98).
    // Counter-clockwise on the drawing (Z right, X up) - so G03, not G02.
    var program = "G21\nG98\nT0101\nG00 X20 Z0\nG03 X40 Z-10 I0 K-10 F100\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    var expected = 10.0 / (500 * 25.4) * 60 + (10.0 * Math.PI / 2) / 100 * 60;
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

// ---- G-code system A: single canned cycles, strictness, decimal codes ----

// 56. G90 OD turning cycle: rapid X in, feed Z, feed X out, rapid Z back - ending where it started,
// having cut the commanded diameter over the commanded length.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X60 Z2\nG90 X50 Z-20 F0.2\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[56] G90 OD turning cycle");
    Check("no alarms", alarms.Count == 0);
    Check("returns to the start point X60 Z2", Math.Abs(sim.X - 60) < 0.01 && Math.Abs(sim.Z - 2) < 0.01);
    Check("cut to ~50mm dia at Z-10 (mid-cut)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 50) < 0.5);
    Check("stock beyond the cut (Z-30) still raw ~76.2mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -30)] - 76.2) < 0.5);
}

// 57. G90 is modal: a following block with only a new X repeats the cycle at that depth without
// restating the G-code. This is the whole point of a "single canned cycle".
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X60 Z2\nG90 X55 Z-20 F0.2\nX50\nX45\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[57] G90 is modal - bare X blocks repeat the cycle deeper");
    Check("no alarms", alarms.Count == 0);
    Check("final pass reached ~45mm dia", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -10)] - 45) < 0.5);
    Check("still parked at the start point", Math.Abs(sim.X - 60) < 0.01 && Math.Abs(sim.Z - 2) < 0.01);
}

// 58. G80 cancels the modal cycle - a coordinate block after it is an ordinary move again, not
// another cycle pass.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X60 Z2\nG90 X50 Z-20 F0.2\nG80\nG00 X40 Z5\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[58] G80 cancels the modal single cycle");
    Check("no alarms", alarms.Count == 0);
    Check("the post-G80 block moved the tool rather than cycling", Math.Abs(sim.X - 40) < 0.01 && Math.Abs(sim.Z - 5) < 0.01);
}

// 59. G94 end-face turning cycle - the facing analogue of G90, so it faces to the commanded Z.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG00 X76.2 Z2\nG94 X30 Z-2 F0.2\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out _);
    Console.WriteLine("[59] G94 end face turning cycle");
    Check("no alarms", alarms.Count == 0);
    Check("returns to the start point", Math.Abs(sim.X - 76.2) < 0.01 && Math.Abs(sim.Z - 2) < 0.01);
    // Facing to Z-2 removes everything outside X30 between the original face and Z-2, so the
    // profile there collapses to the cut diameter; material behind the cut is untouched.
    Check("faced away to ~30mm dia at Z-1 (inside the faced region)", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -1)] - 30) < 0.5);
    Check("stock behind the face (Z-5) still raw ~76.2mm", Math.Abs(sim.Stock.OuterX[NearestIndex(sim.Stock, -5)] - 76.2) < 0.5);
}

// 60. G92 thread cutting cycle needs a threading tool, like every other threading path here.
{
    var sim = new LatheSimulator();
    var alarms = RunFull(sim, new GCodeParser().Parse("G21\nT0101\nG00 X30 Z2\nG92 X28 Z-20 F1.5\nM30\n"), out _);
    Console.WriteLine("[60] G92 thread cutting cycle rejects a non-threading tool");
    Check("alarm 85 raised for the OdTurning tool", alarms.Exists(a => a.Number == 85));

    var sim2 = new LatheSimulator();
    var alarms2 = RunFull(sim2, new GCodeParser().Parse("G21\nT0404\nG00 X30 Z2\nG92 X28 Z-20 F1.5\nM30\n"), out _);
    Check("no alarms with T0404 (threading)", alarms2.Count == 0);
    Check("returns to the start point", Math.Abs(sim2.X - 30) < 0.01 && Math.Abs(sim2.Z - 2) < 0.01);
}

// 61. Unknown codes now alarm instead of being silently ignored, which is what a real control does
// (P/S 010). This is the check that keeps the simulator honest about what it actually supports.
{
    var sim = new LatheSimulator();
    var alarms = RunFull(sim, new GCodeParser().Parse("G21\nT0101\nG73\nM30\n"), out _);
    Console.WriteLine("[61] Unsupported codes raise an improper-G-code alarm");
    Check("unsupported G73 alarms", alarms.Exists(a => a.Number == 10));

    // M112 is Y-AXIS CLAMP: a real FANUC/Leadwell code, but marked X on the base LTC-208 -
    // this machine has no Y axis. Commanding it is a programming error, so it must still alarm.
    // (This check used M63 until the manual turned up; M63 is really chuck low-pressure mode,
    // an option this machine can have, so it stopped being an example of "unsupported".)
    var sim2 = new LatheSimulator();
    var alarms2 = RunFull(sim2, new GCodeParser().Parse("G21\nT0101\nM112\nM30\n"), out _);
    Check("M112 (Y-axis clamp, not fitted) alarms", alarms2.Exists(a => a.Number == 10));

    // ...but the inert modal codes a real control carries must NOT alarm.
    var sim3 = new LatheSimulator();
    var alarms3 = RunFull(sim3, new GCodeParser().Parse("G21\nG18\nG49\nG64\nG22\nG25\nG69\nT0101\nM30\n"), out _);
    Check("accepted-but-inert codes (G18/G49/G64/G22/G25/G69) do not alarm", alarms3.Count == 0);
}

// 62. Decimal-suffixed codes are distinct codes, not variants of their integer part. Before the
// parser kept them separately, G50.1 truncated to G50 (spindle clamp) and G40.1 to G40 (comp
// cancel) - silently doing the wrong thing rather than nothing.
{
    var sim = new LatheSimulator();
    var alarms = RunFull(sim, new GCodeParser().Parse("G21\nT0101\nG13.1\nG50.1\nG40.1\nG80.4\nG69.1\nM30\n"), out _);
    Console.WriteLine("[62] Decimal G-codes are parsed as distinct codes and accepted");
    Check("no alarms for the real machine's inert decimal codes", alarms.Count == 0);

    // G50.1 must not have been mistaken for G50, which would have clamped the spindle.
    Check("G50.1 did not clamp max spindle speed the way G50 S would", sim.Modal.MaxCssRpm == null);

    // G41 then G40.1 - comp must still be active, because G40.1 is not G40.
    var sim2 = new LatheSimulator();
    RunFull(sim2, new GCodeParser().Parse("G21\nT0101\nG41\nG40.1\nM30\n"), out _);
    Check("G40.1 did not cancel cutter compensation the way G40 would", sim2.Modal.Comp == CutterComp.Left);

    var sim3 = new LatheSimulator();
    var alarms3 = RunFull(sim3, new GCodeParser().Parse("G21\nT0101\nG77.3\nM30\n"), out _);
    Check("an unknown decimal code still alarms", alarms3.Exists(a => a.Number == 10));
}

// 63. M02 ends the program like M30 (the difference on a real control is only the rewind), and the
// inferred auxiliary M-codes are accepted rather than alarming.
{
    var sim = new LatheSimulator();
    var blocks = new GCodeParser().Parse("G21\nT0101\nG00 X50 Z2\nM02\nG00 X10 Z10\n");
    var result = sim.RunProgram(blocks);
    Console.WriteLine("[63] M02 ends the program; auxiliary M-codes are accepted");
    Check("program reported as ended", result.ProgramEnded);
    Check("the block after M02 never ran", Math.Abs(sim.X - 50) < 0.01 && Math.Abs(sim.Z - 2) < 0.01);

    // The manual's own auxiliary codes for this machine, including the three the previously
    // guessed table had wrong: the parts catcher is M14/M15 (was guessed M21/M22), the conveyor
    // is M37/M38 (was guessed M52/M53), and M21/M22 are really the door interlock bypass.
    var sim2 = new LatheSimulator();
    var alarms2 = RunFull(sim2, new GCodeParser().Parse(
        "G21\nT0101\nM10\nM11\nM12\nM13\nM14\nM15\nM19\nM20\nM21\nM22\nM37\nM38\nM67\nM97\nM30\n"), out _);
    Check("manual-listed auxiliary M-codes do not alarm", alarms2.Count == 0);
}

// 64. G28 honours an intermediate point when the block gives one, instead of always going straight
// to the reference position.
{
    var sim = new LatheSimulator();
    var program = "G21\nT0101\nG0 X76.2 Z2\nG1 X60 Z-10 F0.2\nG28 X40 Z5\nM30\n";
    var alarms = RunFull(sim, new GCodeParser().Parse(program), out var warnings);
    Console.WriteLine("[64] G28 routes via its intermediate point");
    Check("no alarms", alarms.Count == 0);
    Check("ends at the reference position",
        Math.Abs(sim.X - LatheSimulator.ReferenceX) < 0.01 && Math.Abs(sim.Z - LatheSimulator.ReferenceZ) < 0.01);
    // The intermediate point should appear in the toolpath before the final home move.
    var viaIndex = sim.ToolPath.FindIndex(p => Math.Abs(p.X - 40) < 0.01 && Math.Abs(p.Z - 5) < 0.01);
    Check("the intermediate point X40 Z5 was actually visited", viaIndex >= 0);
}

// ---- Inch as the power-on default, and unit conversion of the F word ----

// 65. The control powers on in inch, like the reference machine, so coordinates in a program that
// never declares units are inches.
{
    var sim = new LatheSimulator();
    Console.WriteLine("[65] Power-on default is inch");
    Check("Modal.Units defaults to Inch", sim.Modal.Units == UnitsMode.Inch);

    var alarms = RunFull(sim, new GCodeParser().Parse("T0101\nG00 X2 Z1\nM30\n"), out _);
    Check("no alarms", alarms.Count == 0);
    Check("X2 inch is held internally as 50.8mm", Math.Abs(sim.X - 50.8) < 0.001);
    Check("Z1 inch is held internally as 25.4mm", Math.Abs(sim.Z - 25.4) < 0.001);
}

// 66. The F word is a dimension and has to convert like one. This was the latent bug: FeedRate was
// assigned raw, so an inch-mode F0.01 became 0.01mm/rev internally instead of 0.254mm/rev - a 25x
// feed error that only stayed invisible while metric was the default.
{
    var sim = new LatheSimulator();
    RunFull(sim, new GCodeParser().Parse("G20\nT0101\nM03 S500\nG01 Z-1 F0.01\nM30\n"), out _);
    Console.WriteLine("[66] An inch-mode F word converts to mm internally");
    Check("F0.01 in/rev is held as 0.254 mm/rev", Math.Abs(sim.FeedRate - 0.254) < 1e-6);

    var sim2 = new LatheSimulator();
    RunFull(sim2, new GCodeParser().Parse("G21\nT0101\nM03 S500\nG01 Z-10 F0.25\nM30\n"), out _);
    Check("a metric F word is unchanged", Math.Abs(sim2.FeedRate - 0.25) < 1e-6);
}

// 67. Cycle time must come out right in inch too - the same closed-form check as the metric cases,
// which is what makes the F conversion above verifiable end to end rather than just internally.
{
    var sim = new LatheSimulator();
    // G98 inch: 2 inch of travel at 4 in/min = 30s.
    var alarms = RunFull(sim, new GCodeParser().Parse("G20\nG98\nT0101\nG01 Z-2 F4\nM30\n"), out _);
    Console.WriteLine("[67] Cycle time is correct in inch mode");
    Check("no alarms", alarms.Count == 0);
    Check("30s for 2in at 4in/min", Math.Abs(sim.SimulatedSecondsElapsed - 30.0) < 0.001);
}

// 68. G96 constant surface speed in inch mode. S is surface FEET per minute in inch and m/min in
// metric, so the two modes need different constants against the mm diameter held internally -
// using the metric one in inch overstated spindle speed by 3.28x.
{
    var sim = new LatheSimulator();
    // 400 SFM at 2.0 inch diameter -> 400 * 12 / (pi * 2.0) = 763.94 rpm.
    RunFull(sim, new GCodeParser().Parse("G20\nT0101\nG00 X2 Z0.1\nG96 S400\nM03\nM30\n"), out _);
    var expectedInch = 400.0 * 12.0 / (Math.PI * 2.0);
    Console.WriteLine("[68] G96 surface speed uses SFM in inch mode");
    Check($"~{expectedInch:F0} rpm at 2in dia from S400 SFM", Math.Abs(sim.SpindleSpeed - expectedInch) < 1.0);

    var sim2 = new LatheSimulator();
    // 150 m/min at 50mm diameter -> 150 * 1000 / (pi * 50) = 954.9 rpm, unchanged behaviour.
    RunFull(sim2, new GCodeParser().Parse("G21\nT0101\nG00 X50 Z2\nG96 S150\nM03\nM30\n"), out _);
    var expectedMetric = 150.0 * 1000.0 / (Math.PI * 50.0);
    Check($"~{expectedMetric:F0} rpm at 50mm dia from S150 m/min", Math.Abs(sim2.SpindleSpeed - expectedMetric) < 1.0);
}

// ---- [69] RELATIVE counter has its own zeroable origin ----
{
    var sim = new LatheSimulator();
    Console.WriteLine("[69] RELATIVE (U/W) counter is independent of ABSOLUTE");

    RunFull(sim, new GCodeParser().Parse("G21\nT0101\nG00 X50 Z10\n"), out _);
    // Untouched, the relative origin is 0, so U/W read the same as X/Z.
    Check("U/W mirror X/Z before any zeroing", Math.Abs(sim.RelativeU - sim.X) < 1e-6 && Math.Abs(sim.RelativeW - sim.Z) < 1e-6);

    sim.ZeroRelativeBoth();
    Check("both read zero right after ALL ZERO", Math.Abs(sim.RelativeU) < 1e-6 && Math.Abs(sim.RelativeW) < 1e-6);
    Check("zeroing the counter never moves the machine", Math.Abs(sim.X - 50) < 1e-6 && Math.Abs(sim.Z - 10) < 1e-6);

    RunFull(sim, new GCodeParser().Parse("G00 X30 Z-5\n"), out _);
    Check("U/W then count the distance travelled since", Math.Abs(sim.RelativeU - (-20)) < 1e-6 && Math.Abs(sim.RelativeW - (-15)) < 1e-6);
    Check("ABSOLUTE is unaffected by the relative origin", Math.Abs(sim.X - 30) < 1e-6 && Math.Abs(sim.Z - (-5)) < 1e-6);

    // Per-axis zeroing is the whole point of the separate U ZERO / W ZERO keys.
    sim.ZeroRelativeW();
    Check("W ZERO clears W and leaves U alone", Math.Abs(sim.RelativeW) < 1e-6 && Math.Abs(sim.RelativeU - (-20)) < 1e-6);
}

Console.WriteLine();
// ---- [70] BLOCK SKIP honours the leading '/' ----
{
    Console.WriteLine("[70] BLOCK SKIP (BDT) honours a leading '/'");
    var prog = "G21\nT0101\nG00 X50 Z10\n/G00 X20\nG00 Z5\nM30\n";

    // Switch off: the slash means nothing and the block runs.
    var off = new LatheSimulator();
    RunFull(off, new GCodeParser().Parse(prog), out _);
    Check("with BLOCK SKIP off the '/' block runs", Math.Abs(off.X - 20) < 1e-6);

    // Switch on: the block is skipped, so X never leaves 50.
    var on = new LatheSimulator { BlockSkip = true };
    RunFull(on, new GCodeParser().Parse(prog), out _);
    Check("with BLOCK SKIP on the '/' block is skipped", Math.Abs(on.X - 50) < 1e-6);
    Check("the rest of the program still runs", Math.Abs(on.Z - 5) < 1e-6);

    // The slash must not survive into the parsed block, or the tokenizer would see garbage.
    var parsed = new GCodeParser().Parse("/G00 X20\n")[0];
    Check("the '/' is stripped and flagged, not left in the code", parsed.IsBlockDelete && parsed.RawCode.StartsWith("G00"));
    Check("a normal block is not flagged as block-delete", !new GCodeParser().Parse("G00 X20\n")[0].IsBlockDelete);
}

// ---- [71] OPT STOP arms M01 ----
{
    Console.WriteLine("[71] OPT STOP (OSP) decides whether M01 pauses");
    var prog = "G21\nT0101\nG00 X50 Z10\nM01\nG00 X20\nM30\n";

    var off = new LatheSimulator();
    var offResult = off.RunProgram(new GCodeParser().Parse(prog), 0);
    Check("with OPT STOP off M01 does not pause", !offResult.Paused && offResult.ProgramEnded);
    Check("so the program runs to the end", Math.Abs(off.X - 20) < 1e-6);

    var on = new LatheSimulator { OptionalStop = true };
    var onResult = on.RunProgram(new GCodeParser().Parse(prog), 0);
    Check("with OPT STOP on M01 pauses", onResult.Paused);
    Check("and it pauses before the following move", Math.Abs(on.X - 50) < 1e-6);

    on.RunProgram(new GCodeParser().Parse(prog), onResult.NextBlockIndex);
    Check("resuming finishes the program", Math.Abs(on.X - 20) < 1e-6);
}

// ---- [72] SINGLE BLOCK steps one block at a time ----
{
    Console.WriteLine("[72] SINGLE BLOCK (SBK) stops after each block");
    var prog = "G21\nT0101\nG00 X50 Z10\nG01 X40 F0.2\nG01 Z-5\nM30\n";

    var continuous = new LatheSimulator();
    RunFull(continuous, new GCodeParser().Parse(prog), out _);

    var stepped = new LatheSimulator { SingleBlock = true };
    var blocks = new GCodeParser().Parse(prog);
    int steps = 0, next = 0;
    while (steps < 50)
    {
        var r = stepped.RunProgram(blocks, next);
        steps++;
        if (r.ProgramEnded || !r.Paused) break;
        next = r.NextBlockIndex;
    }
    Check("it takes more than one Cycle Start to finish", steps > 1);
    Check("stepping reaches the same X as a continuous run", Math.Abs(stepped.X - continuous.X) < 1e-6);
    Check("stepping reaches the same Z as a continuous run", Math.Abs(stepped.Z - continuous.Z) < 1e-6);
    Check("it terminates rather than looping forever", steps < 50);
}

// ---- [73] Spindle speed is clamped to the machine maximum ----
{
    Console.WriteLine("[73] Spindle clamps to the machine maximum, not alarms");
    var sim = new LatheSimulator();
    var alarms = RunFull(sim, new GCodeParser().Parse("G21\nT0101\nG97 S9000\nM03\nM30\n"), out _);
    Check("over-speed is not an alarm", alarms.Count == 0);
    Check($"S9000 clamps to {MachineSpec.MaxSpindleRpm:F0}", Math.Abs(sim.SpindleSpeed - MachineSpec.MaxSpindleRpm) < 1e-6);

    var under = new LatheSimulator();
    RunFull(under, new GCodeParser().Parse("G21\nT0101\nG97 S1200\nM03\nM30\n"), out _);
    Check("a speed under the limit is untouched", Math.Abs(under.SpindleSpeed - 1200) < 1e-6);

    // Under CSS a small diameter asks for enormous rpm; the machine ceiling must still apply.
    var css = new LatheSimulator();
    RunFull(css, new GCodeParser().Parse("G21\nT0101\nG00 X2 Z2\nG96 S200\nM03\nM30\n"), out _);
    Check("CSS is clamped by the machine ceiling too", css.SpindleSpeed <= MachineSpec.MaxSpindleRpm + 1e-6);
}

// ---- [74] The turret has as many stations as the machine ----
{
    Console.WriteLine("[74] Offset table covers every turret station");
    var offsets = new OffsetTables();
    Check($"{MachineSpec.TurretStations} stations exist", offsets.Tools.Count == MachineSpec.TurretStations);
    Check("station 1 exists", offsets.Tools.ContainsKey(1));
    Check("the last station exists", offsets.Tools.ContainsKey(MachineSpec.TurretStations));
}

Console.WriteLine();
// ---- [75] O0015 actually demonstrates what its own comments claim ----
{
    Console.WriteLine("[75] O0015 panel-switch demo behaves as documented");
    var text = File.ReadAllText(@"..\NCFiles\O0015_panel_switch_demo.nc");

    var plain = new LatheSimulator();
    var plainAlarms = RunFull(plain, new GCodeParser().Parse(text), out var plainWarnings);
    Check("runs clean with every switch off", plainAlarms.Count == 0 && plainWarnings.Count == 0);

    // BLOCK SKIP must change the outcome, or the '/' blocks in the file are decorative.
    var skipped = new LatheSimulator { BlockSkip = true };
    var skipAlarms = RunFull(skipped, new GCodeParser().Parse(text), out _);
    Check("runs clean with BLOCK SKIP on too", skipAlarms.Count == 0);
    Check("BLOCK SKIP leaves the OD fatter (a roughing pass was genuinely skipped)",
          skipped.Stock.OuterX[NearestIndex(plain.Stock, -20)] >= plain.Stock.OuterX[NearestIndex(plain.Stock, -20)]);

    // OPT STOP must actually stop, and resuming must reach the same place.
    var stopping = new LatheSimulator { OptionalStop = true };
    var blocks = new GCodeParser().Parse(text);
    var r = stopping.RunProgram(blocks, 0);
    Check("OPT STOP pauses the demo at its M01", r.Paused);
    int guard = 0;
    while (r.Paused && guard++ < 20)
        r = stopping.RunProgram(blocks, r.NextBlockIndex);
    Check("resuming runs it to the end", r.ProgramEnded);
    Check("and lands where the uninterrupted run landed", Math.Abs(stopping.Z - plain.Z) < 1e-6);
}

// ---- [76] A saved offset file from an 8-station build still fills the whole turret ----
{
    Console.WriteLine("[76] Loading an older 8-station offset file backfills the turret");
    var tmp = Path.Combine(Path.GetTempPath(), "fanuc_offsets_8station_test.json");

    // Write a file describing only stations 1-8, exactly as an earlier build would have saved.
    var trimmed = new OffsetTables();
    foreach (var n in trimmed.Tools.Keys.Where(k => k > 8).ToList())
        trimmed.Tools.Remove(n);
    trimmed.SaveToFile(tmp);

    var loaded = OffsetTables.LoadOrDefault(tmp);
    Check("every turret station is present after loading an 8-station file",
          loaded.Tools.Count == MachineSpec.TurretStations);
    Check("the stations the file did not mention still exist",
          loaded.Tools.ContainsKey(MachineSpec.TurretStations));
    Check("a station the file did describe is preserved", loaded.Tools.ContainsKey(1));

    File.Delete(tmp);
}

// ---- [77] The auxiliary M-codes match the machine's own manual ----
{
    Console.WriteLine("[77] Auxiliary M-codes match the LTC-208 manual");

    // Codes the manual lists for the base machine must be accepted...
    var accepted = new LatheSimulator();
    var acceptedAlarms = RunFull(accepted, new GCodeParser().Parse(
        "G21\nT0101\nM07\nM14\nM15\nM17\nM18\nM23\nM24\nM29\nM31\nM32"
        + "\nM33\nM34\nM37\nM38\nM47\nM48\nM49\nM51\nM52\nM55\nM56"
        + "\nM57\nM58\nM67\nM68\nM97\nM30\n"), out _);
    Check("every manual-listed base-machine code is accepted", acceptedAlarms.Count == 0);

    // ...and codes the manual marks X on this machine must not be, because the equipment is not
    // there: push-bar, sub-spindle, Cs-axis, live tooling, Y axis.
    foreach (var notFitted in new[] { "M16", "M59", "M60", "M76", "M90", "M93", "M109", "M110", "M112" })
    {
        var sim = new LatheSimulator();
        var alarms = RunFull(sim, new GCodeParser().Parse("G21\nT0101\n" + notFitted + "\nM30\n"), out _);
        Check($"{notFitted} (not fitted on this machine) alarms", alarms.Exists(a => a.Number == 10));
    }

    // The three the old guessed table got wrong, pinned by the description the engine logs so they
    // cannot quietly drift back to the guessed meanings.
    var reassigned = new LatheSimulator();
    RunFull(reassigned, new GCodeParser().Parse("G21\nT0101\nM21\nM14\nM37\nM30\n"), out _);
    var log = string.Join(" | ", reassigned.Messages);
    Check("M21 is the door interlock bypass, not a parts catcher",
          log.Contains("M21: Door interlock bypass on"));
    Check("M14 is the parts catcher", log.Contains("M14: Parts catcher extend"));
    Check("M37 is the chip conveyor", log.Contains("M37: Chip conveyor CW"));
}

// ---- [78] Playback timeline is faithful on every demo program ----
{
    Console.WriteLine("[78] Playback timeline replays every demo program exactly");
    var stockRx = new System.Text.RegularExpressions.Regex(
        @"\(STOCK:\s*([0-9.]+)\s*(MM|IN)\s*(?:OD|DIA)\s*X\s*([0-9.]+)\s*(MM|IN)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var path in Directory.GetFiles(@"..\NCFiles", "*.nc").OrderBy(p => p))
    {
        var text = File.ReadAllText(path);
        var sim = new LatheSimulator();
        var m = stockRx.Match(text);
        if (m.Success)
        {
            var inch = m.Groups[2].Value.Equals("IN", StringComparison.OrdinalIgnoreCase);
            var lenInch = m.Groups[4].Success ? m.Groups[4].Value.Equals("IN", StringComparison.OrdinalIgnoreCase) : inch;
            sim.StockDiameter = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * (inch ? 25.4 : 1);
            sim.StockLength = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) * (lenInch ? 25.4 : 1);
            sim.ResetStockProfile();
        }
        var chunks = CheckTimelinesThroughout(sim, new GCodeParser().Parse(text), Path.GetFileName(path));
        Check($"{Path.GetFileName(path)} ({chunks} chunk{(chunks == 1 ? "" : "s")})", chunks > 0);
    }
}

// ---- [79] Playback cursor mid-move: position, distance to go, line, partial carve ----
{
    Console.WriteLine("[79] Playback cursor is correct part-way through a cut");
    // Lines: 1 G21, 2 G98, 3 T0101, 4 G00 X50 Z2, 5 G01 Z-50 F100, 6 M30.
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse("G21\nG98\nT0101\nG00 X50 Z2\nG01 Z-50 F100\nM30\n"));
    var tl = sim.LastTimeline!;
    var cut = tl.Events.Single(e => e.Kind == TimelineEventKind.Feed);
    Check("the feed move takes 31.2s (52mm @ 100mm/min)", Math.Abs(cut.Duration - 31.2) < 1e-9);
    // X0 -> X50 is 25mm of travel, the longer axis (Z moves 2), so that axis sets the time.
    Check("the rapid before it takes 25mm (the longer axis) @ 500in/min",
        Math.Abs(cut.StartTime - 25.0 / (500 * 25.4) * 60) < 1e-9);

    var cursor = new PlaybackCursor(tl);
    cursor.Seek(cut.StartTime + cut.Duration / 2);
    var pos = cursor.ToolProgrammed!.Value;
    Check("halfway: tool at X50 Z-24", Math.Abs(pos.X - 50) < 1e-9 && Math.Abs(pos.Z - (-24)) < 1e-9);
    var dtg = cursor.DistanceToGo;
    Check("halfway: distance to go is Z-26, X0", Math.Abs(dtg.X) < 1e-9 && Math.Abs(dtg.Z - (-26)) < 1e-9);
    Check("halfway: the running block is line 5 (the G01)", cursor.CurrentLine == 5);
    Check("halfway: spindle state comes from the run, feed is 100", cursor.State is { } st && Math.Abs(st.FeedRate - 100) < 1e-9);
    Check("halfway: stock already cut behind the tool (Z-10 is at X50)",
        Math.Abs(cursor.Stock.OuterX[NearestIndex(cursor.Stock, -10)] - 50) < 0.5);
    Check("halfway: stock not yet cut ahead of the tool (Z-40 still X76.2)",
        Math.Abs(cursor.Stock.OuterX[NearestIndex(cursor.Stock, -40)] - 76.2) < 1e-9);
    Check("halfway: the log shows the running block, not the ones after it",
        cursor.RevealedLog.Messages < sim.Messages.Count);

    // Scrubbing backwards rebuilds rather than leaving later cuts behind.
    cursor.Seek(cut.StartTime + cut.Duration * 0.1);
    Check("scrub back: Z-10 is uncut again", Math.Abs(cursor.Stock.OuterX[NearestIndex(cursor.Stock, -10)] - 76.2) < 1e-9);
}

// ---- [80] Arcs and dwells: exact timing inside the timeline ----
{
    Console.WriteLine("[80] Arc chords share the arc's exact time; dwells are timed events");
    var sim = new LatheSimulator();
    // A true quarter circle in radius terms: (10,0) about (10,-10) to (20,-10), counter-clockwise.
    sim.RunProgram(new GCodeParser().Parse("G21\nG98\nT0101\nG00 X20 Z0\nG03 X40 Z-10 I0 K-10 F100\nG04 P1500\nM30\n"));
    var tl = sim.LastTimeline!;
    var arcTime = tl.Events.Where(e => e.Line == 5).Sum(e => e.Duration);
    Check("the arc's chords add up to radius x sweep at F100", Math.Abs(arcTime - (10.0 * Math.PI / 2) / 100 * 60) < 1e-9);
    Check("the arc was recorded as several chords", tl.Events.Count(e => e.Line == 5) > 5);
    var dwell = tl.Events.Single(e => e.Kind == TimelineEventKind.Dwell);
    Check("G04 P1500 is a 1.5s dwell event on line 6", Math.Abs(dwell.Duration - 1.5) < 1e-9 && dwell.Line == 6);
}

// ---- [81] A rapid into the part is found at the right moment ----
{
    Console.WriteLine("[81] Playback finds a collision where it happens");
    // The first rapid of a run is exempt (see [10]); the second plows through untouched stock.
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse("G21\nT0101\nG00 X100 Z5\nG00 X40 Z-20\nG00 X100\nM30\n"));
    var tl = sim.LastTimeline!;
    var crash = tl.Events.FirstOrDefault(e => e.Kind == TimelineEventKind.Collision);
    Check("the plunging rapid is recorded as a collision", crash != null && crash.Line == 4);
    var cursor = new PlaybackCursor(tl);
    Check("FirstCollisionBetween finds its start time",
        crash != null && cursor.FirstCollisionBetween(0, tl.Duration) == crash.StartTime);
    Check("and nothing before the first rapid finishes",
        cursor.FirstCollisionBetween(0, tl.Events[0].EndTime - 1e-6) == null);
}

// ---- [82] Chunks split by stops replay to the same part as one uninterrupted run ----
{
    Console.WriteLine("[82] OPT STOP and SINGLE BLOCK chunks replay to the same part");
    var text = File.ReadAllText(@"..\NCFiles\O0015_panel_switch_demo.nc");

    var whole = new LatheSimulator();
    RunFull(whole, new GCodeParser().Parse(text), out _);

    foreach (var (name, sim) in new[]
    {
        ("OPT STOP", new LatheSimulator { OptionalStop = true }),
        ("SINGLE BLOCK", new LatheSimulator { SingleBlock = true }),
    })
    {
        var chunks = CheckTimelinesThroughout(sim, new GCodeParser().Parse(text), name);
        Check($"{name}: every chunk's timeline is faithful ({chunks} chunks)", chunks > 1);
        var same = true;
        for (int i = 0; i <= StockProfile.Resolution; i++)
            same &= sim.Stock.OuterX[i] == whole.Stock.OuterX[i] && sim.Stock.InnerX[i] == whole.Stock.InnerX[i];
        Check($"{name}: final part identical to the uninterrupted run", same);
    }
}

// ---- [83] Playback's log keeps pace with the tool, even inside a canned cycle ----
{
    Console.WriteLine("[83] The log revealed during playback never runs ahead of the tool");

    // A G71 cycle logs every pass from inside one block. Revealing the log block by block showed
    // "Roughing complete" while the tool was still on its first cut; found by live testing.
    var sim = new LatheSimulator { StockDiameter = 50, StockLength = 80 };
    sim.ResetStockProfile();
    sim.RunProgram(new GCodeParser().Parse(File.ReadAllText(@"..\NCFiles\O0003_roughing_finishing_g71_g70.nc")));
    var tl = sim.LastTimeline!;
    var cursor = new PlaybackCursor(tl);

    var firstCut = tl.Events.First(e => e.Kind == TimelineEventKind.Feed);
    cursor.Seek(firstCut.StartTime + firstCut.Duration / 2);
    var shown = sim.Messages.Take(cursor.RevealedLog.Messages).ToList();
    Check("during the first cut, the cycle is not yet reported complete",
        !shown.Any(m => m.Contains("Roughing complete")));
    Check("but the cut in progress has been introduced", shown.Count > 0);

    var complete = sim.Messages.FindIndex(m => m.Contains("Roughing complete"));
    cursor.Seek(tl.Duration);
    Check("by the end, everything including 'Roughing complete' is shown",
        complete >= 0 && cursor.RevealedLog.Messages == sim.Messages.Count);

    // Revealing only ever moves forward as the playhead does.
    var monotonic = true;
    var previous = -1;
    for (int i = 0; i <= 400; i++)
    {
        cursor.Seek(tl.Duration * i / 400);
        monotonic &= cursor.RevealedLog.Messages >= previous;
        previous = cursor.RevealedLog.Messages;
    }
    Check("the revealed log never shrinks as playback advances", monotonic);

    // A collision warning appears when that rapid is reached, not a move early.
    var crashSim = new LatheSimulator();
    crashSim.RunProgram(new GCodeParser().Parse("G21\nT0101\nG00 X100 Z5\nG00 X40 Z-20\nG00 X100\nM30\n"));
    var crashTl = crashSim.LastTimeline!;
    var crash = crashTl.Events.First(e => e.Kind == TimelineEventKind.Collision);
    var crashCursor = new PlaybackCursor(crashTl);
    crashCursor.Seek(crash.StartTime - 1e-6);
    Check("just before the colliding rapid, its warning is not yet shown", crashCursor.RevealedLog.Warnings == 0);
    crashCursor.Seek(crash.StartTime);
    Check("once it starts, the warning is shown", crashCursor.RevealedLog.Warnings == 1);
}

// ---- [84] Playback reports the last N-number reached ----
{
    Console.WriteLine("[84] Playback shows the last N-number reached, even N-only blocks");
    // N10 and N30 are blocks with nothing but an N word: they take no time, so they can only be
    // seen through what the timeline recorded, never by sampling the running line.
    var sim = new LatheSimulator();
    sim.RunProgram(new GCodeParser().Parse("G21\nT0101\nN10\nG98 G01 X50 Z-10 F100\nN30\nG01 Z-20\nM30\n"));
    var tl = sim.LastTimeline!;
    var cursor = new PlaybackCursor(tl);
    // A program whose first N word comes after a move reports none during that move.
    var early = new LatheSimulator();
    early.RunProgram(new GCodeParser().Parse("G21\nT0101\nG98 G01 X50 Z-10 F100\nN30\nG01 Z-20\nM30\n"));
    var earlyCursor = new PlaybackCursor(early.LastTimeline!);
    var earlyCut = early.LastTimeline!.Events.First(e => e.Line == 3);
    earlyCursor.Seek(earlyCut.StartTime + earlyCut.Duration / 2);
    Check("before any N-numbered block is reached, none is reported", earlyCursor.SequenceNumber == -1);


    var firstCut = tl.Events.First(e => e.Line == 4);
    cursor.Seek(firstCut.StartTime + firstCut.Duration / 2);
    Check("during the first cut, N10 is the last N reached", cursor.SequenceNumber == 10);

    var secondCut = tl.Events.First(e => e.Line == 6);
    cursor.Seek(secondCut.StartTime + secondCut.Duration / 2);
    Check("during the second cut, N30 is the last N reached", cursor.SequenceNumber == 30);
}

// ---- [85] X is a diameter: geometry and timing happen in radius terms ----
{
    Console.WriteLine("[85] X is a diameter - arcs, timing and G71 steps work in radius terms");
    const double RapidMmPerMin = 500 * 25.4;

    // Every arc point, in radius terms, is `radius` from the center (given with X as a diameter).
    bool OnCircle(LatheSimulator s, double centerXDia, double centerZ, double radius, int fromIndex) =>
        s.ToolPath.Skip(fromIndex).All(p =>
            Math.Abs(Math.Sqrt(Math.Pow((p.X - centerXDia) / 2, 2) + Math.Pow(p.Z - centerZ, 2)) - radius) < 1e-6);

    // The textbook quarter fillet: 10mm up the shoulder in radius (20mm on the diameter), 10mm along
    // Z, R10. A diameter-space engine rejected it as "radius too small" (ALARM 38).
    var fillet = new LatheSimulator();
    var filletAlarms = RunFull(fillet, new GCodeParser().Parse(
        "G21\nG98\nT0101\nG00 X20 Z2\nG01 Z-10 F100\nG02 X40 Z-20 R10\nM30\n"), out _);
    Check("a standard R10 quarter fillet (X20 Z-10 -> X40 Z-20) runs without alarm", filletAlarms.Count == 0);
    var filletStart = fillet.ToolPath.FindLastIndex(p => Math.Abs(p.Z - (-10)) < 1e-9 && Math.Abs(p.X - 20) < 1e-9);
    // Going up a shoulder, G02 is the concave fillet: its center is out in the open corner, at X40
    // (radius 20) Z-10, not inside the material at X20 Z-20.
    Check("its points form a true R10 concave fillet about X40 Z-10",
        filletStart >= 0 && OnCircle(fillet, 40, -10, 10, filletStart));

    // The textbook direction check (helmancnc's radius-dimensioning example): from the face, G03
    // turns the convex corner and G02 the concave fillet at the next shoulder. The engine once
    // measured arc angles from X toward Z, which swapped the two.
    var textbook = new LatheSimulator { StockDiameter = 80, StockLength = 120 };
    textbook.ResetStockProfile();
    var textbookAlarms = RunFull(textbook, new GCodeParser().Parse(
        "G21\nG98\nT0101\nG00 X0 Z2\nG01 Z0 F100\nG01 X30\nG03 X50 Z-10 R10\nG01 Z-40\nG02 X70 Z-50 R10\nG01 Z-100\nM30\n"), out _);
    var cornerStart = textbook.ToolPath.FindLastIndex(p => Math.Abs(p.Z) < 1e-9 && Math.Abs(p.X - 30) < 1e-9);
    var cornerEnd = textbook.ToolPath.FindIndex(p => Math.Abs(p.Z - (-10)) < 1e-9 && Math.Abs(p.X - 50) < 1e-9);
    var filletFrom = textbook.ToolPath.FindLastIndex(p => Math.Abs(p.Z - (-40)) < 1e-9 && Math.Abs(p.X - 50) < 1e-9);
    var filletTo = textbook.ToolPath.FindIndex(p => Math.Abs(p.Z - (-50)) < 1e-9 && Math.Abs(p.X - 70) < 1e-9);
    bool ArcOn(int from, int to, double cx, double cz) =>
        from >= 0 && to > from && textbook.ToolPath.Skip(from).Take(to - from + 1).All(p =>
            Math.Abs(Math.Sqrt(Math.Pow((p.X - cx) / 2, 2) + Math.Pow(p.Z - cz, 2)) - 10) < 1e-6);
    Check("textbook G03 X50 Z-10 R10 from X30 Z0 is the convex corner (center X30 Z-10)",
        textbookAlarms.Count == 0 && ArcOn(cornerStart, cornerEnd, 30, -10));
    Check("textbook G02 X70 Z-50 R10 from X50 Z-40 is the concave fillet (center X70 Z-40)",
        ArcOn(filletFrom, filletTo, 70, -40));

    // I is a radius value: I10 from X20 puts the center at X40 (diameter), 10mm out from the tool.
    var ik = new LatheSimulator();
    var ikAlarms = RunFull(ik, new GCodeParser().Parse(
        "G21\nG98\nT0101\nG00 X20 Z0\nG02 X40 Z-10 I10 K0 F100\nM30\n"), out _);
    var ikStart = ik.ToolPath.FindLastIndex(p => Math.Abs(p.Z) < 1e-9 && Math.Abs(p.X - 20) < 1e-9);
    Check("I is a radius: G02 X40 Z-10 I10 K0 from X20 Z0 is a true R10 arc about X40 Z0",
        ikAlarms.Count == 0 && ikStart >= 0 && OnCircle(ik, 40, 0, 10, ikStart));
    Check("that arc takes its true length (10 * pi/2 @ 100mm/min) after a 10mm rapid",
        Math.Abs(ik.SimulatedSecondsElapsed - (10.0 / RapidMmPerMin * 60 + 10 * Math.PI / 2 / 100 * 60)) < 1e-6);

    // A feed across 20mm of diameter moves the tool 10mm.
    var face = new LatheSimulator();
    RunFull(face, new GCodeParser().Parse("G21\nG98\nT0101\nG01 X20 F600\nM30\n"), out _);
    Check("G01 X0 -> X20 @ F600 mm/min takes 1.0s (10mm of real travel)",
        Math.Abs(face.SimulatedSecondsElapsed - 1.0) < 1e-9);

    // A rapid takes as long as its longer axis needs - each axis runs at its own rapid rate.
    var diag = new LatheSimulator();
    RunFull(diag, new GCodeParser().Parse("G21\nT0101\nG00 X20 Z-30\nM30\n"), out _);
    Check("G00 X0 Z0 -> X20 Z-30 takes as long as the 30mm Z travel",
        Math.Abs(diag.SimulatedSecondsElapsed - 30.0 / RapidMmPerMin * 60) < 1e-9);

    // G71 U2 is 2mm per side: 4mm off the diameter each pass. From X52 to X30 is 22mm of diameter,
    // so 6 passes (5 full + a last partial), not the 11 a diameter-space step took.
    var g71 = new LatheSimulator { StockDiameter = 50, StockLength = 40 };
    g71.ResetStockProfile();
    RunFull(g71, new GCodeParser().Parse(
        "G21\nG99\nT0101\nM03 S1000\nG00 X52 Z2\nG71 U2 R0.5\nG71 P10 Q20 U0 W0 F0.2\nN10 G00 X30\nN20 G01 Z-20\nM30\n"), out _);
    Check("G71 U2 steps 4mm on the diameter: X52 -> X30 in 6 passes",
        g71.Messages.Any(m => m.Contains("Roughing complete, 6 passes")));
}

// ---- [86] G74/G75 X-direction values are radial (per side) ----
{
    Console.WriteLine("[86] G74/G75: P and the G75 retract are radial, so they count double on X");

    // G75 P1000 = 1mm per side = 2mm on the diameter: X40 -> X30 in 5 pecks, not 10.
    var groove = new LatheSimulator();
    groove.Offsets.GetOrCreateTool(1).Type = ToolType.Grooving;
    groove.RunProgram(new GCodeParser().Parse(
        "G21\nG99\nT0101\nM03 S800\nG00 X40 Z-10\nG75 X30 Z-10 P1000 F0.05\nM30\n"));
    var plunges = groove.LastTimeline!.Events.Count(e => e.Kind == TimelineEventKind.Feed && e.Line == 6);
    Check("G75 P1000 from X40 to X30 is 5 pecks of 1mm per side", plunges == 5);

    // Each peck backs off R per side: R0.5 -> 1mm on the diameter.
    var backOffs = groove.LastTimeline!.Events
        .Where(e => e.Kind == TimelineEventKind.Rapid && e.Line == 6 && e.ToX > e.FromX)
        .Select(e => e.ToX - e.FromX).ToList();
    Check("G75 backs off 0.5mm per side (1mm on X) between pecks",
        backOffs.Count >= 4 && backOffs.Take(4).All(d => Math.Abs(d - 1.0) < 1e-9));

    // G74 P2000 = 2mm per side = 4mm on the diameter: X0 -> X10 at X0, 4, 8, 10.
    var drill = new LatheSimulator();
    drill.Offsets.GetOrCreateTool(2).Type = ToolType.Drill;
    drill.Offsets.GetOrCreateTool(2).Width = 6;
    drill.RunProgram(new GCodeParser().Parse(
        "G21\nG99\nT0202\nM03 S800\nG00 X0 Z2\nG74 X10 Z-5 P2000 Q2000 F0.1\nM30\n"));
    Check("G74 P2000 from X0 to X10 steps 4mm on the diameter (4 positions)",
        drill.Messages.Any(m => m.Contains("4 X position(s)")));
}

// ---- [87] G71/G72 finishing shape must be monotonic: PS0064 / PS0329 ----
{
    Console.WriteLine("[87] G71/G72 shape rules: type I (one axis in the P block) has no pockets");
    List<Alarm> Rough(string pBlock, string rest)
    {
        var sim = new LatheSimulator { StockDiameter = 50, StockLength = 80 };
        sim.ResetStockProfile();
        return RunFull(sim, new GCodeParser().Parse(
            "G21\nG99\nT0101\nM03 S1000\nG00 X52 Z2\nG71 U2 R0.5\nG71 P10 Q90 U0.4 W0.1 F0.2\n" +
            pBlock + "\n" + rest + "\nM30\n"), out _);
    }

    // A profile that grows steadily toward the chuck - fine as type I.
    var plain = Rough("N10 G00 X30", "N20 G01 Z-20\nN30 X40 Z-30\nN90 G01 Z-50");
    Check("type I, steadily growing profile: no alarm", plain.Count == 0);

    // The same shape with a relief groove in it (X40 -> X34 -> X40): a pocket.
    const string pocket = "N20 G01 Z-20\nN30 X40\nN40 Z-30\nN50 X34\nN60 Z-35\nN70 X40\nN90 G01 Z-50";
    var typeI = Rough("N10 G00 X30", pocket);
    Check("type I (only X in the P block) with a pocket: PS0329", typeI.Any(a => a.Number == 329));
    var typeII = Rough("N10 G00 X30 Z2", pocket);
    Check("type II (X and Z in the P block) with the same pocket: no alarm", typeII.Count == 0);

    // Doubling back along Z is never allowed, type II included.
    var backZ = Rough("N10 G00 X30 Z2", "N20 G01 Z-20\nN30 X40 Z-15\nN90 G01 Z-50");
    Check("a profile that doubles back along Z: PS0064, even as type II", backZ.Any(a => a.Number == 64));
}

// ---- [88] Spindle ramp and surface finish ----
{
    Console.WriteLine("[88] Spindle ramp: SAR waits for speed; without it the start of a cut is rough");
    var rate = MachineSpec.MaxSpindleRpm / MachineSpec.SpindleRampSecondsTypical;

    // The model itself: 0 -> 1500 RPM at 1500 RPM/s takes 1s and turns 12.5 revs (average 750 RPM).
    var model = new SpindleModel(rate);
    Check("ramp: 0 -> 1500 RPM takes 1s", Math.Abs(model.SecondsToReach(1500) - 1.0) < 1e-12);
    Check("ramp: 12.5 revolutions in that first second", Math.Abs(model.RevolutionsOver(1.0, 1500) - 12.5) < 1e-9);
    Check("ramp: and back - 12.5 revs take 1s", Math.Abs(model.SecondsForRevolutions(12.5, 1500) - 1.0) < 1e-9);
    Check("ramp: 37.5 revs = 1s ramping + 1s at 1500", Math.Abs(model.SecondsForRevolutions(37.5, 1500) - 2.0) < 1e-9);
    var reversing = new SpindleModel(rate) { ActualRpm = 1500 };
    Check("reversal ramps down through zero: M03 1500 -> M04 1500 takes 2s", Math.Abs(reversing.SecondsToReach(-1500) - 2.0) < 1e-12);
    Check("reversal: 25 revs over those 2s (12.5 down, 12.5 up)", Math.Abs(reversing.RevolutionsOver(2.0, -1500) - 25) < 1e-9);

    // The same cut straight after M03, with SAR on and off. G99 at 0.2mm/rev, R0.4 nose (T1).
    const string Cut = "G21\nG99\nT0101\nM03 S1500\nG00 X40 Z1\nG01 Z-40 F0.2\nM30\n";
    LatheSimulator RunCut(bool sar)
    {
        var s = new LatheSimulator { StockDiameter = 50, StockLength = 60, SpindleSpeedArrivalCheck = sar };
        s.ResetStockProfile();
        s.RunProgram(new GCodeParser().Parse(Cut));
        return s;
    }
    var withSar = RunCut(true);
    var noSar = RunCut(false);
    var steadyRa = 0.2 * 0.2 / (32 * 0.4) * 1000; // 3.125 um
    double RaAt(LatheSimulator s, double z) => s.Stock.OuterRa[NearestIndex(s.Stock, z)];

    Check("SAR on: the control waited for the spindle", withSar.Messages.Any(m => m.StartsWith("SAR: waiting")));
    Check("SAR on: the whole cut has the steady finish, Ra = f^2/32r = 3.125um",
        Math.Abs(RaAt(withSar, -1) - steadyRa) < 1e-9 && Math.Abs(RaAt(withSar, -35) - steadyRa) < 1e-9);
    Check("SAR off: no wait, and the log says the cut started before speed",
        !noSar.Messages.Any(m => m.StartsWith("SAR: waiting")) && noSar.Messages.Any(m => m.StartsWith("Cutting before the spindle")));
    Check("SAR off: the start of the cut is rougher than steady", RaAt(noSar, -0.5) > steadyRa * 1.5);
    Check("SAR off: once up to speed the finish is steady again", Math.Abs(RaAt(noSar, -35) - steadyRa) < 1e-9);
    Check("SAR off: the cut takes longer than steady, the tool crawling while the spindle is slow",
        noSar.SimulatedSecondsElapsed > 41.0 / (0.2 * 1500) * 60);
    Check("the end-of-program summary names the roughest finish",
        noSar.Messages.Any(m => m.StartsWith("Surface finish: roughest Ra")));

    // G98 (per-minute) feed doesn't follow the spindle: a slow spindle means more feed per rev.
    var perMin = new LatheSimulator { StockDiameter = 50, StockLength = 60, SpindleSpeedArrivalCheck = false };
    perMin.ResetStockProfile();
    perMin.RunProgram(new GCodeParser().Parse("G21\nG98\nT0101\nM03 S1500\nG00 X40 Z1\nG01 Z-40 F300\nM30\n"));
    Check("G98 with SAR off: rough start, steady (F300/1500 = 0.2mm/rev) once at speed",
        RaAt(perMin, -0.5) > steadyRa * 1.5 && Math.Abs(RaAt(perMin, -35) - steadyRa) < 1e-9);

    // A ramp of 0 is an instant spindle: no wait, and the finish is steady from the first mm.
    var instant = new LatheSimulator { StockDiameter = 50, StockLength = 60, SpindleSpeedArrivalCheck = false, SpindleRampSeconds = 0 };
    instant.ResetStockProfile();
    instant.RunProgram(new GCodeParser().Parse(Cut));
    Check("ramp 0: steady finish from the start", Math.Abs(RaAt(instant, -0.5) - steadyRa) < 1e-9);

    // Replay reproduces the rough start exactly (the shared timeline invariants, finish included).
    var replay = new LatheSimulator { StockDiameter = 50, StockLength = 60, SpindleSpeedArrivalCheck = false };
    replay.ResetStockProfile();
    Check("SAR off: playback replays the ramped cut and its finish exactly",
        CheckTimelinesThroughout(replay, new GCodeParser().Parse(Cut), "SAR off cut") == 1);
}

// ---- [89] FEED and RAPID override dials ----
{
    Console.WriteLine("[89] Override dials: FEED scales cutting feed (not threading) and finish; RAPID scales rapids");
    const double RapidMmPerMin = 500 * 25.4;

    // G98 F300 over 41mm (Z1 -> Z-40), spindle already at speed (no ramp), stock 50: at 100% vs 50%.
    LatheSimulator Cut(double feedOverride)
    {
        var s = new LatheSimulator { StockDiameter = 50, StockLength = 60, SpindleRampSeconds = 0, FeedOverride = feedOverride };
        s.ResetStockProfile();
        s.RunProgram(new GCodeParser().Parse("G21\nG98\nT0101\nM03 S1500\nG01 X40 Z1 F300\nG01 Z-40\nM30\n"));
        return s;
    }
    var full = Cut(1.0);
    var half = Cut(0.5);
    var cutFull = full.LastTimeline!.Events.Single(e => e.Line == 6);
    var cutHalf = half.LastTimeline!.Events.Single(e => e.Line == 6);
    Check("FEED 50%: the cut takes twice as long", Math.Abs(cutHalf.Duration - 2 * cutFull.Duration) < 1e-9);
    Check("FEED 50%: finish is a quarter (Ra goes with feed per rev squared)",
        Math.Abs(cutHalf.CarveRa - cutFull.CarveRa / 4) < 1e-9);
    Check("the timeline records the override it was run at", half.LastTimeline!.RecordedFeedOverride == 0.5);
    Check("cutting moves are marked as scaled by the FEED dial", cutHalf.FeedOverrideApplies);
    Check("rapids are not", !half.LastTimeline!.Events.Where(e => e.Kind == TimelineEventKind.Rapid).Any(e => e.FeedOverrideApplies));

    // Threading ignores the FEED override: the lead is locked to the spindle.
    double ThreadSeconds(double feedOverride)
    {
        var s = new LatheSimulator { SpindleRampSeconds = 0, FeedOverride = feedOverride };
        s.Offsets.GetOrCreateTool(3).Type = ToolType.Threading;
        s.RunProgram(new GCodeParser().Parse("G21\nG99\nT0303\nM03 S600\nG00 X20 Z2\nG32 Z-20 F1.5\nM30\n"));
        return s.LastTimeline!.Events.Single(e => e.Line == 6).Duration;
    }
    Check("G32 threading takes the same time at FEED 50% as at 100%", Math.Abs(ThreadSeconds(0.5) - ThreadSeconds(1.0)) < 1e-12);

    // RAPID: 25% is four times as long; F0 runs at parameter 1421's speed.
    double RapidSeconds(int percent, double f0 = 1000)
    {
        var s = new LatheSimulator { RapidOverridePercent = percent, RapidF0MmPerMin = f0 };
        s.RunProgram(new GCodeParser().Parse("G21\nT0101\nG00 Z-30\nM30\n"));
        return s.SimulatedSecondsElapsed;
    }
    Check("RAPID 100%: 30mm at 500 in/min", Math.Abs(RapidSeconds(100) - 30 / RapidMmPerMin * 60) < 1e-9);
    Check("RAPID 25%: four times as long", Math.Abs(RapidSeconds(25) - 4 * RapidSeconds(100)) < 1e-9);
    Check("RAPID F0: at the parameter 1421 rate (1000 mm/min here)", Math.Abs(RapidSeconds(0, 1000) - 30.0 / 1000 * 60) < 1e-9);

    // Live dials during playback: the cursor plays each event at its own rate.
    var tl = full.LastTimeline!;
    var cursor = new PlaybackCursor(tl);
    double Doubled(TimelineEvent e) => e.Kind == TimelineEventKind.Feed ? 2.0 : 1.0;
    Check("at twice the rate, the feed moves take half the machine time",
        Math.Abs(cursor.MachineSecondsBetween(cutFull.StartTime, cutFull.EndTime, Doubled) - cutFull.Duration / 2) < 1e-9);
    cursor.Seek(cutFull.StartTime);
    var (after, stalled) = cursor.TimeAfter(cutFull.Duration / 4, Doubled);
    Check("TimeAfter: a quarter of the cut's time at double rate gets halfway through it",
        !stalled && Math.Abs(after - (cutFull.StartTime + cutFull.Duration / 2)) < 1e-9);
    var (stuck, isStalled) = cursor.TimeAfter(5.0, e => e.Kind == TimelineEventKind.Feed ? 0 : 1);
    Check("TimeAfter: FEED at 0% stops the playhead in a cut, machine time still passing",
        isStalled && Math.Abs(stuck - cutFull.StartTime) < 1e-9);

    // Turning the dial mid-cut changes the finish of the rest of the cut only.
    cursor.FinishScale = 1.0;
    cursor.Seek(cutFull.StartTime + cutFull.Duration / 2);
    cursor.FinishScale = 0.25;
    cursor.Seek(tl.Duration);
    double RaAt(StockProfile st, double z) => st.OuterRa[NearestIndex(st, z)];
    Check("dial turned mid-cut: the first half keeps its finish", Math.Abs(RaAt(cursor.Stock, -5) - cutFull.CarveRa) < 1e-9);
    Check("dial turned mid-cut: the second half takes the new one", Math.Abs(RaAt(cursor.Stock, -35) - cutFull.CarveRa * 0.25) < 1e-9);
    Check("dial turned mid-cut: the shape is still exactly the engine's",
        Enumerable.Range(0, StockProfile.Resolution + 1).All(i => Math.Abs(cursor.Stock.OuterX[i] - full.Stock.OuterX[i]) < 1e-9));
}

// ---- [90] The tool's round nose: what a real insert cuts ----
{
    Console.WriteLine("[90] Nose radius: tapers and corners cut by a round nose, and what G42 does about it");
    const double R = 0.4; // T1's nose

    LatheSimulator Cut(string body, double stockDia = 50)
    {
        var s = new LatheSimulator { StockDiameter = stockDia, StockLength = 60, SpindleRampSeconds = 0 };
        s.ResetStockProfile();
        s.RunProgram(new GCodeParser().Parse("G21\nG99\nT0101\nM03 S1000\n" + body + "\nM30\n"));
        return s;
    }
    double OdAt(LatheSimulator s, double z, out double sampleZ)
    {
        var i = NearestIndex(s.Stock, z);
        sampleZ = s.Stock.SampleZ(i);
        return s.Stock.OuterX[i];
    }

    // A 45-degree chamfer (X20 -> X30 over Z5) without comp: the nose leaves r(2 - sqrt2) on the
    // radius - the classic reason for nose-radius comp.
    var chamfer = Cut("G00 X20 Z2\nG01 Z0 F0.1\nX30 Z-5\nZ-20\nG00 X52");
    var odC = OdAt(chamfer, -2.5, out var zC);
    var programmedC = 2 * (10 + (-zC));
    Check($"no comp: the 45-degree chamfer is left oversize by 2r(2 - sqrt2) = {2 * R * (2 - Math.Sqrt(2)):F4} on X",
        Math.Abs(odC - programmedC - 2 * R * (2 - Math.Sqrt(2))) < 1e-6);
    Check("no comp: the straight turn after it is exact (X30)", Math.Abs(OdAt(chamfer, -12, out _) - 30) < 1e-6);

    var chamferComp = Cut("G00 X20 Z2\nG01 Z0 F0.1\nG42\nX30 Z-5\nZ-20\nG40\nG00 X52");
    Check("G42: the same chamfer comes out on the programmed line",
        Math.Abs(OdAt(chamferComp, -2.5, out var zC2) - 2 * (10 + (-zC2))) < 1e-6);

    // An inside corner (turn X30 to Z-20, then up the shoulder to X44): the nose can't reach into
    // it, leaving a fillet of its own radius - with or without comp.
    var shoulder = Cut("G00 X30 Z2\nG01 Z-20 F0.1\nX44\nZ-30\nG00 X52");
    var odS = OdAt(shoulder, -20 + R / 2, out var zS);
    var filletX = 2 * (15 + R - Math.Sqrt(R * R - Math.Pow(zS - (-20 + R), 2)));
    Check($"no comp: an inside corner keeps a R{R} fillet (X{filletX:F4} at Z{zS:F3})", Math.Abs(odS - filletX) < 1e-6);
    Check("no comp: past the corner the shoulder face is clean - no gouge into it",
        OdAt(shoulder, -20.3, out _) >= 44 - 1e-6);

    var shoulderComp = Cut("G00 X30 Z2\nG42\nG01 Z-20 F0.1\nX44\nZ-30\nG40\nG00 X52");
    Check("G42 inside corner: the nose stops at the corner - the shoulder face is not gouged",
        OdAt(shoulderComp, -20.3, out _) >= 44 - 1e-6);
    Check("G42 inside corner: the turned diameter is exact right up to the fillet",
        Math.Abs(OdAt(shoulderComp, -19.2, out _) - 30) < 1e-6);

    // A straight line into a non-tangent arc at an inside corner (the O0008 thread-blank shape): the
    // arc's first chords lie wholly inside the corner and must be swallowed, not followed.
    double Programmed(double z) =>
        z >= -10 ? 14.6 + 0.06 * (-z)
        : z >= -12.5 ? 2 * (7.136 + Math.Sqrt(Math.Max(0, 9 - Math.Pow(z + 12.964, 2))))
        : 20.2;
    var lineArc = Cut("G00 X14.6 Z2\nG42\nG01 Z0 F0.15\nX15.2 Z-10\nG03 X20.2 Z-12.5 R3\nG01 Z-44\nG40\nG00 X40", 22);
    var worstGouge = 0.0;
    for (int i = 0; i <= StockProfile.Resolution; i++)
    {
        var z = lineArc.Stock.SampleZ(i);
        if (z < -40 || z > -0.2) continue;
        worstGouge = Math.Min(worstGouge, lineArc.Stock.OuterX[i] - Programmed(z));
    }
    Check($"G42 line into an arc at an inside corner: no gouge beyond chord error (worst {worstGouge:F4})", worstGouge > -0.01);

    // Rapids are checked by the real nose, not the imaginary tip: pulling straight out at the end of
    // a pass (the tip level with the cut, the nose clear of it) is not a collision.
    Check("pulling out at the end of a pass is not flagged as a collision", shoulder.Warnings.Count == 0);

    // Playback replays the round-nose carves - held, trimmed and swallowed ones included - exactly.
    var replay = new LatheSimulator { StockDiameter = 22, StockLength = 60, SpindleRampSeconds = 0 };
    replay.ResetStockProfile();
    Check("playback replays nose carves under G42 exactly",
        CheckTimelinesThroughout(replay, new GCodeParser().Parse(
            "G21\nG99\nT0101\nM03 S1000\nG00 X14.6 Z2\nG42\nG01 Z0 F0.15\nX15.2 Z-10\nG03 X20.2 Z-12.5 R3\nG01 Z-44\nG40\nG00 X40\nM30\n"),
            "line-arc G42") == 1);
}

// ---- [91] Cycle P/Q words count in the least input increment of the active units ----
{
    Console.WriteLine("[91] G74/G75/G76 P and Q: 0.001mm under G21, 0.0001in under G20 (checked against CIMCO Edit)");
    // The O0014 groove: G75 X1.90 Z-1.60 P400 Q400 from X2.40 Z-1.50, in inch. Q400 = 0.040in steps
    // along Z (Z-1.50, -1.54, -1.58, -1.60); P400 = 0.040in per side = 0.080in on X per peck.
    var sim = new LatheSimulator();
    sim.Offsets.GetOrCreateTool(5).Type = ToolType.Grooving;
    sim.RunProgram(new GCodeParser().Parse(
        "G20\nG99\nT0505\nM03 S500\nG00 X2.40 Z-1.50\nG75 R0.02\nG75 X1.90 Z-1.60 P400 Q400 F0.004\nM30\n"));
    var plungeZs = sim.LastTimeline!.Events
        .Where(e => e.Kind == TimelineEventKind.Feed && e.Line == 7)
        .Select(e => Math.Round(e.ToZ / 25.4, 4)).Distinct().OrderByDescending(z => z).ToList();
    Check($"inch G75 Q400 steps 0.040in along Z: {string.Join(", ", plungeZs)}",
        plungeZs.SequenceEqual(new[] { -1.50, -1.54, -1.58, -1.60 }));
    var firstPeck = sim.LastTimeline!.Events.First(e => e.Kind == TimelineEventKind.Feed && e.Line == 7);
    Check("inch G75 P400 pecks 0.040in per side: X2.40 -> X2.32",
        Math.Abs(firstPeck.ToX / 25.4 - 2.32) < 1e-9);

    // The same words in a metric program are thousandths of a mm.
    var metric = new LatheSimulator();
    metric.Offsets.GetOrCreateTool(5).Type = ToolType.Grooving;
    metric.RunProgram(new GCodeParser().Parse(
        "G21\nG99\nT0505\nM03 S500\nG00 X40 Z-10\nG75 R0.5\nG75 X30 Z-11 P1000 Q500 F0.05\nM30\n"));
    var metricPeck = metric.LastTimeline!.Events.First(e => e.Kind == TimelineEventKind.Feed && e.Line == 7);
    Check("metric G75 P1000 pecks 1mm per side: X40 -> X38", Math.Abs(metricPeck.ToX - 38) < 1e-9);
}

// ---- [92] G71 passes: stepped from the start point, cutting only where material is left ----
{
    Console.WriteLine("[92] G71 roughing: passes step in from the start X, and never re-trace finished stretches");
    // O0003's roughing: start X52 Z2, U2 (2mm per side = 4 on X), finish allowance U0.5 W0.1. The
    // shape holds X40 to Z-25, runs down an R10 arc to X30, then steps to X24 - a recess.
    var sim = new LatheSimulator { StockDiameter = 50, StockLength = 80 };
    sim.ResetStockProfile();
    sim.RunProgram(new GCodeParser().Parse(
        "G21\nG99\nT0101\nM03 S800\nG00 X52 Z2\nG71 U2 R1\nG71 P10 Q80 U0.5 W0.1 F0.25\n" +
        "N10 G00 X40 Z2\nN20 G01 Z-5 F0.15\nN30 X40 Z-25\nN40 G02 X30 Z-35 R10\nN50 G01 Z-50\nN60 X24 Z-50\nN70 Z-65\nN80 X50 Z-65\nM30\n"));
    var cuts = sim.LastTimeline!.Events
        .Where(e => e.Kind == TimelineEventKind.Feed && e.Line == 7 && Math.Abs(e.FromX - e.ToX) < 1e-9 && Math.Abs(e.FromZ - e.ToZ) > 0.2)
        .ToList();
    var levels = cuts.Select(e => Math.Round(e.ToX, 3)).Distinct().OrderByDescending(x => x).ToList();
    // As on the Leadwell: first pass at the start X less one depth (52 - 4 = 48), then 4 at a time,
    // then the finish allowance - 24 + 0.5. X40.5 is the X40 pass riding the finished shape.
    Check($"pass levels step in from the start: {string.Join(", ", levels)}",
        levels.SequenceEqual(new[] { 48.0, 44.0, 40.5, 40.0, 36.0, 32.0, 30.5, 28.0, 24.5 }));
    // The X40 stretch (Z2 to Z-25, finished at X40.5) is cut once, then left alone: the old passes
    // traced it again on every pass below it, in air.
    var overFinished = cuts.Count(e => Math.Abs(e.ToX - 40.5) < 1e-9 && e.ToZ > -25);
    Check($"the finished X40 stretch is cut once, not re-traced ({overFinished} cut(s) at X40.5 over Z0..-25)", overFinished == 2);
    Check("passes below X40 start at the arc, never back at the face",
        cuts.Where(e => e.ToX < 40 - 1e-9).All(e => Math.Max(e.FromZ, e.ToZ) < -25));
    Check("no alarms", sim.Alarms.Count == 0);
}

// ---- [93] G76: flank infeed, finishing allowance, and no nose comp ----
{
    Console.WriteLine("[93] G76 threading pass by pass, as CIMCO Edit's Backplot shows O0005");
    List<(double X, double Z)> Passes(string extra)
    {
        var sim = new LatheSimulator { StockDiameter = 30, StockLength = 50 };
        sim.ResetStockProfile();
        sim.Offsets.GetOrCreateTool(4).Type = ToolType.Threading;
        sim.Offsets.GetOrCreateTool(4).NoseRadius = 0.2;
        sim.RunProgram(new GCodeParser().Parse(
            "G21\nG99\nT0404\nM03 S600\n" + extra + "G00 X24 Z2\nG76 P020060 Q100 R0.05\nG76 X21.8 Z-30 R0 P1100 Q300 F2.0\nM30\n"));
        return sim.LastTimeline!.Events
            .Where(e => e.Kind == TimelineEventKind.Feed && Math.Abs(e.FromX - e.ToX) < 1e-9 && Math.Abs(e.FromZ - e.ToZ) > 5)
            .Select(e => (Math.Round(e.FromX, 3), Math.Round(e.FromZ, 3))).ToList();
    }
    // CIMCO's passes for this cycle (X, start Z): first cut 0.3, then 0.3 x sqrt2 = 0.424, then the
    // 0.1 minimum, rough to 1.1 - 0.05 = 1.05 (X21.9), then P02 = two finishing passes at 1.1 (X21.8).
    // Each shallower pass starts (1.1 - depth) x tan30 further along: flank infeed.
    var expected = new List<(double, double)>
    {
        (23.4, 1.538), (23.152, 1.61), (22.952, 1.668), (22.752, 1.725), (22.552, 1.783), (22.352, 1.841),
        (22.152, 1.899), (21.952, 1.956), (21.9, 1.971), (21.8, 1.971), (21.8, 1.971),
    };
    var passes = Passes("");
    Check($"G76 passes match CIMCO's: {string.Join(" ", passes.Select(p => $"X{p.X}/Z{p.Z}"))}",
        passes.Count == expected.Count && passes.Zip(expected).All(t => Math.Abs(t.First.X - t.Second.Item1) < 0.0015 && Math.Abs(t.First.Z - t.Second.Item2) < 0.0015));
    // G42 left on makes no difference: threading runs the tool as a sharp point.
    Check("G42 left active doesn't offset a threading cycle (zero nose radius assumed)", Passes("G42\n").SequenceEqual(passes));
}

Console.WriteLine($"===== TOTAL: {pass} passed, {fail} failed =====");
Environment.Exit(fail == 0 ? 0 : 1);
