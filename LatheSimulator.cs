using System;
using System.Collections.Generic;
using System.Linq;

namespace FanucSimulator
{
    public class RunResult
    {
        public bool Paused { get; set; }
        public bool ProgramEnded { get; set; }
        public int NextBlockIndex { get; set; }
    }

    public partial class LatheSimulator
    {
        // X/Z are programmed (work) coordinates, shown on the POS screen.
        // X is diameter-programmed, the standard Fanuc lathe convention.
        public double X { get; set; } = 0;
        public double Z { get; set; } = 0;
        public double SpindleSpeed { get; set; } = 0;
        public int SpindleDir { get; set; } = 0; // 0=off, 1=fwd, -1=rev
        public double FeedRate { get; set; } = 0;   // no feed commanded yet, like a real control at power-on
        public int CurrentTool { get; set; } = 0;
        public bool CoolantOn { get; set; } = false;

        public ModalState Modal { get; } = new();
        public OffsetTables Offsets { get; }

        // Realistic cycle-time estimate, accumulated as moves/dwells actually happen - not a wall-
        // clock Stopwatch (the program executes near-instantly regardless of what it commands), a
        // computed estimate of how long the moves/dwells actually commanded would take on a real
        // machine. Reset once per RunProgram call, same lifetime as Messages/Alarms/Warnings.
        public double SimulatedSecondsElapsed { get; private set; }

        // This machine's real rapid (500 in/min) - see MachineSpec for where it came from.
        private const double RapidTraverseRateMmPerMin = MachineSpec.RapidTraverseMmPerMin;

        // ---- Override dials ----

        // FEED override, as a fraction (1.0 = 100%, up to 1.5). Scales every cutting feed except
        // threading. Must be above zero here: at 0% a real machine simply stops feeding, which only
        // playback can show (it holds the tool); a run is recorded at 100% in that case.
        private double _feedOverride = 1.0;
        public double FeedOverride
        {
            get => _feedOverride;
            set => _feedOverride = Math.Clamp(value, 0.01, MachineSpec.MaxFeedOverride);
        }

        // RAPID override: 100, 50 or 25 (%), or 0 for the F0 position, which runs at a fixed speed
        // set by parameter 1421 rather than a percentage.
        public int RapidOverridePercent { get; set; } = 100;
        public double RapidF0MmPerMin { get; set; } = MachineSpec.RapidF0PlaceholderMmPerMin;

        public double RapidRateMmPerMin => RapidOverridePercent <= 0
            ? RapidF0MmPerMin
            : RapidTraverseRateMmPerMin * RapidOverridePercent / 100.0;

        // Set around arc tessellation: TessellateArc drives each ~3-degree chord through MoveTo (for
        // offset/comp/collision-checking reuse), but chord-summed distance is only an approximation
        // of true arc length - ApplyArcMotion instead adds one exact radius*sweep-based time for the
        // whole arc, so per-chord MoveTo calls during tessellation must not also add their own
        // (approximate, and would double-count) time.
        private bool _suppressMoveTimeAccumulation = false;

        private void AddMoveTime(double distanceMm, bool rapid)
        {
            if (_suppressMoveTimeAccumulation)
                return;
            SimulatedSecondsElapsed += MoveSeconds(distanceMm, rapid);
        }

        // How long a move takes on the machine, with no side effects - shared by the cycle-time clock
        // above and the playback timeline, so the two can never disagree about a move's duration.
        // Starts from the spindle's actual speed now: a per-rev feed follows the spindle as it really
        // turns, so while it is still ramping up the tool advances more slowly too.
        private double MoveSeconds(double distanceMm, bool rapid, bool feedOverrideApplies = true)
        {
            if (distanceMm <= 0)
                return 0;

            if (rapid)
                return RapidRateMmPerMin > 0 ? distanceMm / RapidRateMmPerMin * 60 : 0;

            var feed = EffectiveFeed(feedOverrideApplies);
            if (feed <= 0)
                return 0;

            if (Modal.Feed == FeedMode.PerRevolution)
            {
                // Per-rev feed only moves while the spindle turns. Stopped and staying stopped, the
                // real axis would sit waiting forever; there is no honest time to give it, so it is
                // skipped rather than invented (as it always has been here).
                var seconds = Spindle.SecondsForRevolutions(distanceMm / feed, SpindleTargetRpm);
                return double.IsInfinity(seconds) ? 0 : seconds;
            }

            return distanceMm / feed * 60;
        }

        // The feed actually cut at: F scaled by the FEED override dial, except where the override
        // doesn't apply (threading, whose lead must stay locked to the spindle).
        private double EffectiveFeed(bool feedOverrideApplies) => FeedRate * (feedOverrideApplies ? FeedOverride : 1.0);

        // ---- Spindle ramp and surface finish ----

        // The spindle's real speed, ramping toward the commanded one - see SpindleModel.
        public SpindleModel Spindle { get; } = new(MachineSpec.MaxSpindleRpm / MachineSpec.SpindleRampSecondsTypical);

        // Seconds for the spindle to go from stopped to full speed (MachineSpec.MaxSpindleRpm); the
        // ramp is that rate, up or down. 0 = instant.
        public double SpindleRampSeconds
        {
            get => Spindle.RampRpmPerSecond > 0 ? MachineSpec.MaxSpindleRpm / Spindle.RampRpmPerSecond : 0;
            set => Spindle.RampRpmPerSecond = value > 0 ? MachineSpec.MaxSpindleRpm / value : 0;
        }

        // Parameter 3708 bit 0 (SAR): check the spindle speed arrival signal before a cutting move.
        // On, the control holds each cutting move until the spindle is up to the commanded speed. Off,
        // it cuts straight away - while the spindle may still be accelerating, at the wrong surface
        // speed, which leaves a rough start to the cut.
        public bool SpindleSpeedArrivalCheck { get; set; } = true;

        // Within this fraction of the commanded speed counts as "arrived".
        private const double SpindleArrivalBand = 0.01;

        // Signed: + forward (M03), - reverse (M04), 0 stopped.
        private double SpindleTargetRpm => SpindleDir * SpindleSpeed;

        // How much rougher a cut gets when taken below its programmed speed, at the limit of the
        // spindle barely turning: Ra is multiplied by 1 + this * (shortfall fraction). Low cutting
        // speed tears rather than shears the metal (built-up edge), which is well established; this
        // exact factor is an illustrative figure, not a measurement.
        private const double LowSpeedFinishPenalty = 3.0;

        // Past this, "rough" is all there is to say - and it keeps a stopped spindle from reading as
        // an infinite number.
        private const double MaxTrackedRa = 50.0;

        // Ra in micrometres for a turned surface. The geometry: each turn of the part, the tool's
        // round nose leaves a scallop whose height depends on the feed per rev (f) and nose radius
        // (r) - the standard estimate is Ra = f^2 / (32 r). Per-rev feed stays at F even while the
        // spindle ramps (the feed follows the spindle); per-minute feed does not, so a slow spindle
        // means more feed per rev. On top of that, cutting below the programmed speed is penalised.
        private double SurfaceRoughness(double actualRpm, double targetRpm, double noseRadius)
        {
            var r = Math.Max(noseRadius, 0.05);
            var speed = Math.Abs(actualRpm);
            // Only turning and boring are tracked, and the FEED override always applies to them.
            var feed = EffectiveFeed(feedOverrideApplies: true);
            var feedPerRev = Modal.Feed == FeedMode.PerRevolution ? feed
                : speed > 1e-6 ? feed / speed : double.PositiveInfinity;
            var ra = feedPerRev * feedPerRev / (32 * r) * 1000;
            var speedRatio = Math.Abs(targetRpm) > 1e-6 ? Math.Clamp(speed / Math.Abs(targetRpm), 0, 1) : 0;
            ra *= 1 + LowSpeedFinishPenalty * (1 - speedRatio);
            return Math.Min(ra, MaxTrackedRa);
        }

        // How many pieces a cut is split into while the spindle is still ramping, so the finish can
        // change along it - the rough start of a cut taken before the spindle is up to speed.
        private const int RampPieces = 8;

        private readonly record struct MovePiece(double FromFraction, double ToFraction, double Seconds, double Ra, double StartRpm);

        // Plans one move against the spindle as it is now: its time, and (for a cut whose finish is
        // tracked) its pieces with their finish. Pure - the spindle only advances once the move is
        // done.
        private (double Seconds, List<MovePiece> Pieces) PlanMove(double travel, bool rapid, bool trackFinish, double noseRadius, bool feedOverrideApplies)
        {
            var seconds = MoveSeconds(travel, rapid, feedOverrideApplies);
            var target = SpindleTargetRpm;
            var pieces = new List<MovePiece>();
            var rampLeft = Spindle.SecondsToReach(target);

            if (rapid || seconds <= 0 || rampLeft <= 1e-9)
            {
                var ra = !rapid && trackFinish ? SurfaceRoughness(Spindle.ActualRpm, target, noseRadius) : double.NaN;
                pieces.Add(new MovePiece(0, 1, seconds, ra, Spindle.ActualRpm));
                return (seconds, pieces);
            }

            var times = new List<double>();
            var rampEnd = Math.Min(rampLeft, seconds);
            for (int k = 0; k <= RampPieces; k++)
                times.Add(rampEnd * k / RampPieces);
            if (rampEnd < seconds - 1e-12)
                times.Add(seconds);
            times[^1] = seconds;

            var perRev = Modal.Feed == FeedMode.PerRevolution;
            var totalRevs = Spindle.RevolutionsOver(seconds, target);
            double Fraction(double t) => t >= seconds ? 1
                : perRev ? (totalRevs > 0 ? Math.Min(1, Spindle.RevolutionsOver(t, target) / totalRevs) : t / seconds)
                : t / seconds;

            for (int k = 1; k < times.Count; k++)
            {
                var (t0, t1) = (times[k - 1], times[k]);
                var revs = Spindle.RevolutionsOver(t1, target) - Spindle.RevolutionsOver(t0, target);
                var averageRpm = t1 > t0 ? revs / (t1 - t0) * 60 : Math.Abs(Spindle.RpmAfter(t0, target));
                var ra = trackFinish ? SurfaceRoughness(averageRpm, target, noseRadius) : double.NaN;
                pieces.Add(new MovePiece(Fraction(t0), Fraction(t1), t1 - t0, ra, Spindle.RpmAfter(t0, target)));
            }
            return (seconds, pieces);
        }

        // With SAR on, a cutting move first waits for the spindle to reach its commanded speed; the
        // wait is real machine time and plays back as a pause.
        private void WaitForSpindleArrival()
        {
            if (!SpindleSpeedArrivalCheck)
                return;
            var target = SpindleTargetRpm;
            if (target == 0)
                return;
            var gap = Math.Abs(target - Spindle.ActualRpm);
            if (gap <= Math.Max(1.0, Math.Abs(target) * SpindleArrivalBand))
                return;

            var wait = Spindle.SecondsToReach(target);
            if (wait <= 0)
                return;
            Messages.Add($"SAR: waiting {wait:F2}s for the spindle to reach {Math.Abs(target):F0} RPM (at {Math.Abs(Spindle.ActualRpm):F0})");
            SimulatedSecondsElapsed += wait;
            RecordDwell(wait);
            Spindle.Advance(wait, target);
        }

        // The target the "cutting before the spindle is up to speed" note was last given for, so a
        // ramp is reported once rather than on every piece of every cut.
        private double? _rampNoticeTarget;

        // Worst finish left on the part (Z in mm), or null if nothing with a tracked finish was cut.
        public (double Ra, double Z, bool Bore)? RoughestFinish()
        {
            (double Ra, double Z, bool Bore)? worst = null;
            for (int i = 0; i <= StockProfile.Resolution; i++)
            {
                if (!double.IsNaN(Stock.OuterRa[i]) && (worst == null || Stock.OuterRa[i] > worst.Value.Ra))
                    worst = (Stock.OuterRa[i], Stock.SampleZ(i), false);
                if (!double.IsNaN(Stock.InnerRa[i]) && (worst == null || Stock.InnerRa[i] > worst.Value.Ra))
                    worst = (Stock.InnerRa[i], Stock.SampleZ(i), true);
            }
            return worst;
        }

        // The end-of-program summary.
        private void ReportSurfaceFinish()
        {
            if (RoughestFinish() is { } w)
                Messages.Add($"Surface finish: roughest Ra {w.Ra:F2} um, on the {(w.Bore ? "bore" : "OD")} at Z{Len(w.Z)}");
        }

        // ---- Playback timeline ----
        // Recorded alongside the instant run so the UI can play it back at machine speed afterwards.
        // Purely additive: nothing below changes what the engine does, only what it writes down.

        public MotionTimeline? LastTimeline { get; private set; }
        private MotionTimeline? _timeline;
        private int _currentLine;

        // The most recent motion event - cutter-comp mitering has to move its end point, the same way
        // it moves ToolPath's. Dwells never break that chain, so they are not tracked here.
        private TimelineEvent? _lastMotionEvent;

        private MachineStateSnapshot CaptureState(double? actualRpm = null) => new(
            Math.Abs(actualRpm ?? Spindle.ActualRpm), SpindleDir, CoolantOn, CurrentTool, FeedRate,
            Modal.Units == UnitsMode.Inch, Modal.Feed == FeedMode.PerRevolution,
            Modal.Motion, Modal.Comp, Modal.ActiveWorkOffset, Modal.Spindle == SpindleMode.ConstantSurfaceSpeed);

        private void RecordMarker(int line, int sequenceNumber = -1)
        {
            if (_timeline == null)
                return;
            _timeline.Markers.Add(new BlockMarker
            {
                Seq = _timeline.NextSeq(),
                Time = _timeline.Duration,
                Line = line,
                SequenceNumber = sequenceNumber,
                MessageCount = Messages.Count,
                WarningCount = Warnings.Count,
                AlarmCount = Alarms.Count,
                State = CaptureState(),
            });
        }

        private void RecordDwell(double seconds)
        {
            if (_timeline == null)
                return;
            _timeline.Events.Add(new TimelineEvent
            {
                Seq = _timeline.NextSeq(),
                StartTime = _timeline.Duration,
                Duration = seconds,
                Kind = TimelineEventKind.Dwell,
                FromRenderX = _lastActualRenderPos?.X ?? X,
                FromRenderZ = _lastActualRenderPos?.Z ?? Z,
                ToRenderX = _lastActualRenderPos?.X ?? X,
                ToRenderZ = _lastActualRenderPos?.Z ?? Z,
                FromX = X, FromZ = Z, ToX = X, ToZ = Z,
                Line = _currentLine,
                State = CaptureState(),
                MessagesBefore = Messages.Count,
                WarningsBefore = Warnings.Count,
                AlarmsBefore = Alarms.Count,
            });
            _timeline.Duration += seconds;
        }

        public LatheSimulator() : this(new OffsetTables()) { }

        // Lets a caller carry an existing OffsetTables (e.g. loaded from disk, or already edited
        // this session) into a fresh simulator - used by Reset so cycle/alarm/position state clears
        // without wiping tool offsets, matching how a real control's RESET never touches the offset table.
        public LatheSimulator(OffsetTables offsets)
        {
            Offsets = offsets;
            Stock = new StockProfile(StockLength, StockDiameter);
        }

        // Raw stock. Spans Z0 (face) to Z=-StockLength (chuck). Stock is the carved cross-section
        // profile driving the canvas render - not integrated with alarms/collision (that's a later,
        // separate follow-up).
        public double StockDiameter { get; set; } = 76.2; // mm, ~3in, matches the user's own test part
        public double StockLength { get; set; } = 101.6;    // mm
        public StockProfile Stock { get; private set; }

        // Called when the operator changes stock dimensions mid-session - a fresh blank, not a
        // resize of an already-cut part.
        public void ResetStockProfile() => Stock = new StockProfile(StockLength, StockDiameter);

        // Tool path is rendered with tool offset + cutter comp applied; X/Z above stay as programmed coordinates.
        public List<(double X, double Z, string Type)> ToolPath { get; } = new();
        public List<string> Messages { get; } = new();
        public List<Alarm> Alarms { get; } = new();

        // Simulator-only safety advisories - things a real Fanuc control has no way to detect (it has
        // no sensor for "is there material here"), so unlike Alarms these never reach the ALARM screen
        // or the ALM status badge. They're purely a courtesy to whoever's watching this simulator, not
        // something the machine itself would ever know about - it'd just crash the tool and keep going.
        public List<string> Warnings { get; } = new();

        public const double MaxX = 150;
        public const double MaxZ = 300;
        public const double MinZ = -500;
        private const double InchToMm = 25.4;

        // Where G28 parks the turret. On a real lathe the reference position is at the positive
        // extreme of both axes and machine zero sits there, which is why the reference photo of the
        // real control reads MACHINE X-7.6619 Z-7.0527 - the part is at NEGATIVE machine coordinates.
        // This simulator doesn't model a machine origin distinct from the work origin (its MACHINE
        // readout is just work + work offset), so reference return used to mean work X0 Z0: the
        // spindle centreline at the face, i.e. straight through the part. These constants at least
        // put the turret somewhere clear instead. Properly modelling machine-zero-at-reference would
        // let MACHINE read negative like the real screen - a worthwhile, separate change.
        public const double ReferenceX = MaxX;
        public const double ReferenceZ = 150;

        // The RELATIVE (U/W) counter has its own origin that the operator can zero independently of
        // any work offset - it's scratch measurement, used for things like "how far have I moved
        // since I touched off". Zeroing it never affects where the machine actually goes.
        public double RelativeOriginX { get; private set; }
        public double RelativeOriginZ { get; private set; }
        public double RelativeU => X - RelativeOriginX;
        public double RelativeW => Z - RelativeOriginZ;

        public void ZeroRelativeU() => RelativeOriginX = X;
        public void ZeroRelativeW() => RelativeOriginZ = Z;
        public void ZeroRelativeBoth() { RelativeOriginX = X; RelativeOriginZ = Z; }

        private int _activeOffsetNumber = 0;

        // The previous comp-active segment's offset line (a point on it + its direction), used to
        // miter this segment's corner against it. Null when there's nothing to join to.
        private (double X, double Z, double Dx, double Dz)? _pendingCompLine = null;

        // Where the tool actually last ended up, in render coordinates. A tool change alone doesn't
        // move anything, but it does change which offset gets added to the (unchanged) logical X/Z -
        // so the "from" point straight off SelectTool can look like a point this tool never visited.
        // Anchoring the collision check to this last real position instead keeps checking the next
        // *commanded* move (almost always the approach into the part) without flagging the phantom
        // offset jump itself. Null only before the very first move of the run.
        private (double X, double Z)? _lastActualRenderPos = null;

        // The three operator-panel switches that genuinely change how a program runs. They are
        // panel state, not program state: a real control keeps them set across runs and RESET, and
        // nothing in the G-code can turn them on or off, which is why they live here as plain
        // properties rather than in ModalState.
        //
        // SINGLE BLOCK (SBK): stop after each block instead of running to the end.
        // BLOCK SKIP (BDT): honour the leading '/' on a block and skip it.
        // OPTIONAL STOP (OSP): make M01 pause. With it off M01 is a no-op, which is the whole
        //   point of the code - so M01 is inert here by design, not by omission.
        public bool SingleBlock { get; set; }
        public bool BlockSkip { get; set; }
        public bool OptionalStop { get; set; }

        private enum BlockRangeExit { RanOffEnd, Returned, Paused, ProgramEnded }

        // SBK stops after each block that actually did something. Labels and macro control-flow
        // lines are stepped through rather than stopped on: they command no motion, and stopping on
        // every WHILE condition re-test would make single block unusable on a macro program.
        private bool SingleBlockStop(bool isTopLevel) => isTopLevel && SingleBlock;

        public RunResult RunProgram(List<GCodeParser.Block> blocks, int startIndex = 0)
        {
            Messages.Clear();
            Alarms.Clear();
            Warnings.Clear();
            SimulatedSecondsElapsed = 0;

            _timeline = new MotionTimeline(Stock.Clone(), ToolPath.Count)
            {
                RecordedFeedOverride = FeedOverride,
                RecordedRapidMmPerMin = RapidRateMmPerMin,
            };
            LastTimeline = _timeline;
            _lastMotionEvent = null;

            // Only a genuinely fresh run (not a resume after an M00 pause) resets the macro call
            // stack/depth counter - state started by a G65/M98 call must survive an M00 pause and
            // resume within the same logical run, same as every other piece of simulator state does.
            if (startIndex == 0)
            {
                _localVarStack.Clear();
                _callDepth = 0;
                _modalMacroActive = false;
            }

            var (exit, next) = RunBlockRange(blocks, startIndex, isTopLevel: true);
            if (exit != BlockRangeExit.Paused)
                ReportSurfaceFinish();

            // Closing marker: captures the log and machine state after the last block ran, which no
            // block-start marker would otherwise see (e.g. an M05/M09 on the final line).
            RecordMarker(0);
            return exit switch
            {
                BlockRangeExit.Paused => new RunResult { Paused = true, NextBlockIndex = next },
                _ => new RunResult { ProgramEnded = true, NextBlockIndex = 0 },
            };
        }

        // Shared block-dispatch loop for both the top-level program and any nested M98/G65 call -
        // handles canned-cycle setup/trigger, macro control flow (assignment/GOTO/IF/WHILE/END),
        // subprogram and macro calls, and ordinary motion. Only at the outermost (isTopLevel) level
        // do M00/M30 pause or end the run; hitting them inside a nested call just logs and continues
        // (a documented simplification - a real control would alarm/behave specially there, but that
        // is not how any program in this codebase is written).
        private (BlockRangeExit Exit, int NextIndex) RunBlockRange(List<GCodeParser.Block> blocks, int startIndex, bool isTopLevel)
        {
            int i = startIndex;
            while (i < blocks.Count)
            {
                var block = blocks[i];

                if (block.IsLabel)
                {
                    i++;
                    continue;
                }

                // Everything this block records - motion, dwell, log lines - is attributed to its line,
                // which is what lets playback highlight the block that is actually running.
                _currentLine = block.Line;
                RecordMarker(block.Line, block.Params.TryGetValue("N", out var n) ? (int)n : -1);

                // Block delete applies at every level, not just the top - a '/' line inside a
                // subprogram is skipped by the same switch.
                if (BlockSkip && block.IsBlockDelete)
                {
                    Messages.Add($"/ Block skipped (BLOCK SKIP on): {block.RawCode}");
                    i++;
                    continue;
                }

                if (block.Commands.Any(c => c.Type == 'M' && c.Code == 99))
                {
                    Messages.Add("M99: Return from subprogram/macro");
                    return (BlockRangeExit.Returned, i + 1);
                }

                if (block.HasMacroSyntax && block.MacroKind != GCodeParser.MacroKind.None)
                {
                    HandleMacroControlFlow(block, blocks, ref i);
                    continue;
                }

                // A plain block - either always was one, or is a macro-syntax "None" kind (variables
                // inside an ordinary motion/G-code line) that needs substituting into one first.
                var toExecute = block.HasMacroSyntax
                    ? new GCodeParser().ParseLine(SubstituteExpressions(block.RawCode), block.Line)
                    : block;

                if (toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 98))
                {
                    ExecuteM98(toExecute, blocks);
                    i++;
                    continue;
                }

                if (IsRoughingSetup(toExecute))
                {
                    StoreRoughingSetup(toExecute);
                    i++;
                    if (SingleBlockStop(isTopLevel)) return (BlockRangeExit.Paused, i);
                    continue;
                }

                if (IsDrillingSetup(toExecute))
                {
                    StoreDrillingSetup(toExecute);
                    i++;
                    if (SingleBlockStop(isTopLevel)) return (BlockRangeExit.Paused, i);
                    continue;
                }

                if (IsGroovingSetup(toExecute))
                {
                    StoreGroovingSetup(toExecute);
                    i++;
                    if (SingleBlockStop(isTopLevel)) return (BlockRangeExit.Paused, i);
                    continue;
                }

                if (IsThreadingSetup(toExecute))
                {
                    StoreThreadingSetup(toExecute);
                    i++;
                    if (SingleBlockStop(isTopLevel)) return (BlockRangeExit.Paused, i);
                    continue;
                }

                if (IsCannedCycleTrigger(toExecute, out var cycleCode))
                {
                    ExecuteCannedCycleTrigger(cycleCode, toExecute, blocks);

                    // G70/G71/G72 consume an N-numbered contour range (P start, Q end) purely as
                    // geometry data - those blocks must not also run as ordinary motion afterward, or
                    // the tool re-traces (and rapids straight back into) the same contour a second
                    // time. G75/G76 don't reference an N-range this way (G76's own Q is a depth value,
                    // not a sequence number), so they're unaffected.
                    if ((cycleCode == 70 || cycleCode == 71 || cycleCode == 72) &&
                        toExecute.Params.TryGetValue("Q", out var nfVal))
                    {
                        var nf = (int)nfVal;
                        var endIdx = blocks.FindIndex(b => b.Params.TryGetValue("N", out var n) && (int)n == nf);
                        i = endIdx >= 0 ? Math.Max(i + 1, endIdx + 1) : i + 1;
                    }
                    else
                    {
                        i++;
                    }

                    // A canned cycle is one block to the operator, however many moves it makes -
                    // single block stops after the whole cycle, not partway through it.
                    if (SingleBlockStop(isTopLevel)) return (BlockRangeExit.Paused, i);
                    continue;
                }

                TriggerModalMacroIfArmed(toExecute, blocks);
                ExecuteBlock(toExecute);

                if (isTopLevel && toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 0))
                    return (BlockRangeExit.Paused, i + 1);

                // M01 does nothing at all unless the operator has armed OPTIONAL STOP - that is the
                // code's entire purpose, so an inert M01 with the switch off is correct behaviour.
                if (isTopLevel && OptionalStop && toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 1))
                {
                    // ApplyMCode has already logged the M01 and which way the switch is set;
                    // repeating it here just doubles the line in the console.
                    return (BlockRangeExit.Paused, i + 1);
                }

                // M02 and M30 both end the program; on a real control the difference is only that
                // M30 rewinds the cursor to the top, which is what the 0 vs i+1 index expresses.
                if (isTopLevel && toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 30))
                    return (BlockRangeExit.ProgramEnded, 0);

                if (isTopLevel && toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 2))
                    return (BlockRangeExit.ProgramEnded, i + 1);

                i++;
                if (SingleBlockStop(isTopLevel)) return (BlockRangeExit.Paused, i);
            }

            if (!isTopLevel)
                Messages.Add("WARNING: Subprogram/macro ran off end without M99");
            return (BlockRangeExit.RanOffEnd, i);
        }

        private void ExecuteBlock(GCodeParser.Block block)
        {
            foreach (var (_, code) in block.Commands.Where(c => c.Type == 'G'))
                ApplyGCode(code, block);

            foreach (var extended in block.ExtendedCodes)
                ApplyExtendedCode(extended);

            // A T-word indexes the turret and selects its offset by itself on a real lathe - M06
            // is often omitted entirely (unlike a machining center's automatic tool changer, where
            // M06 is mandatory). Handle the T-word here so "T0101" alone works, not only "T0101 M6".
            if (block.Params.ContainsKey("Tool"))
                SelectTool(block);

            foreach (var (_, code) in block.Commands.Where(c => c.Type == 'M'))
                ApplyMCode(code, block);

            if (block.Commands.Any(c => c.Type == 'G' && c.Code == 4))
            {
                ApplyDwell(block);
                return;
            }

            if (block.Commands.Any(c => c.Type == 'G' && c.Code == 28))
            {
                // G28 goes to the reference position via an optional intermediate point. A block
                // giving X/Z (or U/W) rapids there first, which is how programs clear a fixture
                // before homing; a bare G28 goes straight home.
                var (viaX, viaZ, hasVia) = ResolveTargetXZ(X, Z, block);
                if (hasVia)
                {
                    TryMoveTo(viaX, viaZ, rapid: true);
                    Messages.Add($"G28: Via intermediate point X{Len(viaX)} Z{Len(viaZ)}");
                }
                TryMoveTo(ReferenceX, ReferenceZ, rapid: true);
                Messages.Add("G28: Return to reference position");
                return;
            }

            if (block.Commands.Any(c => c.Type == 'G' && c.Code == 50) &&
                (block.Params.ContainsKey("X") || block.Params.ContainsKey("Z")))
            {
                if (block.Params.TryGetValue("X", out var presetX)) X = ToMm(presetX);
                if (block.Params.TryGetValue("Z", out var presetZ)) Z = ToMm(presetZ);
                Messages.Add($"G50: Coordinate preset X{Len(X)} Z{Len(Z)}");
                return;
            }

            if (block.Commands.Any(c => c.Type == 'G' && (c.Code == 32 || c.Code == 33)))
            {
                ExecuteSingleThread(block);
                return;
            }

            // A modal single cycle (G90/G92/G94) swallows any block that carries coordinates while
            // it's armed - that's what "modal" means here: repeating the cycle at a new depth needs
            // only the changed address, not the G-code again. Checked before ordinary motion so an
            // armed cycle never falls through and gets executed as a plain move.
            if (Modal.Cycle != CannedCycle.None)
            {
                var (_, _, cycleHasMotion) = ResolveTargetXZ(X, Z, block);
                if (cycleHasMotion)
                {
                    ExecuteSingleCycle(block);
                    return;
                }
            }

            if (Modal.Motion == MotionMode.ArcCw || Modal.Motion == MotionMode.ArcCcw)
                ApplyArcMotion(block);
            else
                ApplyMotion(block);
        }

        private void ApplyGCode(int code, GCodeParser.Block block)
        {
            switch (code)
            {
                case 0:
                    Modal.Motion = MotionMode.Rapid;
                    Messages.Add("G00: Rapid positioning");
                    break;
                case 1:
                    Modal.Motion = MotionMode.Linear;
                    Messages.Add("G01: Linear interpolation");
                    break;
                case 2:
                    Modal.Motion = MotionMode.ArcCw;
                    break; // message logged once the arc actually resolves, in ApplyArcMotion
                case 3:
                    Modal.Motion = MotionMode.ArcCcw;
                    break;
                case 4:
                    break; // handled in ExecuteBlock via ApplyDwell
                case 20:
                    Modal.Units = UnitsMode.Inch;
                    Messages.Add("G20: Inch mode");
                    break;
                case 21:
                    Modal.Units = UnitsMode.Metric;
                    Messages.Add("G21: Metric mode");
                    break;
                case 28:
                    break; // handled in ExecuteBlock
                case 40:
                    Modal.Comp = CutterComp.Off;
                    _pendingCompLine = null;
                    Messages.Add("G40: Tool nose radius compensation cancel");
                    break;
                case 41:
                    Modal.Comp = CutterComp.Left;
                    _pendingCompLine = null;
                    Messages.Add("G41: Tool nose radius compensation left");
                    break;
                case 42:
                    Modal.Comp = CutterComp.Right;
                    _pendingCompLine = null;
                    Messages.Add("G42: Tool nose radius compensation right");
                    break;
                case 50:
                    if (block.Params.TryGetValue("Speed", out var maxRpm))
                    {
                        Modal.MaxCssRpm = maxRpm;
                        Messages.Add($"G50: Max spindle speed clamped to {maxRpm:F0} RPM");
                    }
                    break;
                case 54: case 55: case 56: case 57: case 58: case 59:
                    Modal.ActiveWorkOffset = code;
                    Messages.Add($"G{code}: Work offset selected");
                    break;
                case 80:
                    // Cancels the modal single cycle (G90/G92/G94). The G70-G76 multiple repetitive
                    // cycles aren't modal and are unaffected - they fire once per trigger block.
                    Modal.Cycle = CannedCycle.None;
                    ResetSingleCycleModals();
                    Messages.Add("G80: Canned cycle cancel");
                    break;
                // G90/G92/G94 are the system-A single canned cycles. They arm the modal group here;
                // the cycle itself runs from ExecuteBlock (see ExecuteSingleCycle) whenever a block
                // carries one of them or, being modal, supplies fresh coordinates while armed.
                // Switching between them clears the remembered coordinates - they aren't shared.
                case 90:
                    if (Modal.Cycle != CannedCycle.Turning) ResetSingleCycleModals();
                    Modal.Cycle = CannedCycle.Turning;
                    break;
                case 92:
                    if (Modal.Cycle != CannedCycle.Threading) ResetSingleCycleModals();
                    Modal.Cycle = CannedCycle.Threading;
                    break;
                case 94:
                    if (Modal.Cycle != CannedCycle.Facing) ResetSingleCycleModals();
                    Modal.Cycle = CannedCycle.Facing;
                    break;
                case 96:
                    Modal.Spindle = SpindleMode.ConstantSurfaceSpeed;
                    if (block.Params.TryGetValue("Speed", out var vc))
                        Modal.SurfaceSpeedVc = vc;
                    Messages.Add($"G96: Constant surface speed @ {Modal.SurfaceSpeedVc:F0} {(Modal.Units == UnitsMode.Inch ? "SFM" : "m/min")}");
                    RecalculateCssSpeed();
                    break;
                case 97:
                    Modal.Spindle = SpindleMode.ConstantRpm;
                    if (block.Params.TryGetValue("Speed", out var rpm))
                        SpindleSpeed = ClampToMachineMaxRpm(rpm);
                    Messages.Add("G97: Constant spindle speed (RPM)");
                    break;
                case 98:
                    Modal.Feed = FeedMode.PerMinute;
                    Messages.Add("G98: Feed per minute");
                    break;
                case 99:
                    Modal.Feed = FeedMode.PerRevolution;
                    Messages.Add("G99: Feed per revolution");
                    break;
                default:
                    if (!ApplyInertGCode(code))
                        Alarms.Add(new Alarm(10, $"G{code:D2}: Improper G-code - not supported by this control"));
                    break;
            }
        }

        // Codes a real 0i-TF accepts and carries as modal state, but which have no effect on a
        // 2-axis turning simulation - their active members need an axis or feature this engine
        // doesn't model (C axis, tool length compensation, polar/cylindrical interpolation, a servo
        // model for exact-stop vs cutting mode).
        //
        // They are tracked rather than merely tolerated: the modal block on the POS screen shows
        // this group, and displaying a hardcoded G22 after the program commanded G23 would be a lie
        // on screen. So the last-commanded member is remembered and displayed - while still doing
        // nothing, which is the honest part. Each entry maps the code to the ModalState field that
        // holds its group.
        private bool ApplyInertGCode(int code)
        {
            switch (code)
            {
                case 17: case 18: case 19: Modal.Plane = code; return true;
                case 22: case 23: Modal.StrokeCheck = code; return true;
                case 25: case 26: Modal.SpeedFluctuationDetect = code; return true;
                case 61: case 64: Modal.CuttingMode = code; return true;
                case 15: case 16: Modal.PolarCommand = code; return true;
                case 68: case 69: Modal.CoordRotation = code; return true;
                case 49: Modal.ToolLengthComp = code; return true;
                case 7: return true; // cylindrical interpolation cancel - no group displayed for it
                default: return false;
            }
        }

        // Same idea for the decimal-suffixed codes the parser keeps in Block.ExtendedCodes: the
        // value is the modal group each belongs to, so commanding one updates what the modal block
        // displays for that group. Every one of these is inert here for the same reasons as above.
        private static readonly Dictionary<string, string> ExtendedCodeGroups = new()
        {
            ["G5.5"] = "HighSpeedCycle",        ["G05.5"] = "HighSpeedCycle",
            ["G5.4"] = "HighSpeedCycle",        ["G05.4"] = "HighSpeedCycle",
            ["G12.1"] = "PolarInterpolation",   ["G13.1"] = "PolarInterpolation",
            ["G40.1"] = "NormalDirection",      ["G41.1"] = "NormalDirection",
            ["G42.1"] = "NormalDirection",
            ["G50.1"] = "MirrorImage",          ["G51.1"] = "MirrorImage",
            ["G50.2"] = "PolygonTurning",       ["G51.2"] = "PolygonTurning",
            ["G68.1"] = "BalancedCutting",      ["G69.1"] = "BalancedCutting",
            ["G80.4"] = "ElectronicGearBoxA",   ["G81.4"] = "ElectronicGearBoxA",
            ["G80.5"] = "ElectronicGearBoxB",   ["G81.5"] = "ElectronicGearBoxB",
        };

        private void ApplyExtendedCode(string code)
        {
            if (!ExtendedCodeGroups.TryGetValue(code, out var group))
            {
                Alarms.Add(new Alarm(10, $"{code}: Improper G-code - not supported by this control"));
                return;
            }
            // Normalise "G5.5" to the "G05.5" spelling the real screen uses for its modal block.
            Modal.ExtendedGroups[group] = code.Length == 4 && code[1] == '5' ? "G0" + code.Substring(1) : code;
        }

        private void ApplyMCode(int code, GCodeParser.Block block)
        {
            switch (code)
            {
                case 0:
                    Messages.Add("M00: Program stop");
                    break;
                case 1:
                    // The pause itself is handled by RunBlockRange, which is the only place that
                    // can actually stop the run; this just reports which way the switch is set.
                    Messages.Add(OptionalStop
                        ? "M01: Optional stop"
                        : "M01: Optional stop (OPT STOP off - ignored)");
                    break;
                case 3:
                    SpindleDir = 1;
                    ApplySpindleSpeedCommand(block);
                    Messages.Add($"M03: Spindle forward @ {SpindleSpeed:F0} RPM");
                    break;
                case 4:
                    SpindleDir = -1;
                    ApplySpindleSpeedCommand(block);
                    Messages.Add($"M04: Spindle reverse @ {SpindleSpeed:F0} RPM");
                    break;
                case 5:
                    SpindleDir = 0;
                    Messages.Add("M05: Spindle stop");
                    break;
                case 6:
                    Messages.Add($"M06: Tool change confirmed (T{CurrentTool:D2}, offset #{_activeOffsetNumber})");
                    break;
                case 8:
                    CoolantOn = true;
                    Messages.Add("M08: Coolant on");
                    break;
                case 9:
                    CoolantOn = false;
                    Messages.Add("M09: Coolant off");
                    break;
                case 2:
                    // Program end without rewind. RunBlockRange ends the run for both M02 and M30;
                    // the difference is only that M30 returns the cursor to the top.
                    Messages.Add("M02: Program end");
                    break;
                case 30:
                    Messages.Add("M30: Program end, rewind");
                    break;
                case 19:
                    Messages.Add("M19: Spindle orient");
                    break;
                case 98:
                    break; // subprogram call is dispatched by RunBlockRange, which logs it there
                case 99:
                    break; // subprogram return likewise - logging here too would double up
                default:
                    if (!IsAcceptedAuxiliaryMCode(code))
                        Alarms.Add(new Alarm(10, $"M{code:D2}: Improper M-code - not supported by this control"));
                    break;
            }
        }

        // How confident we are that an auxiliary M-code number is right for THIS machine. The
        // distinction is the point of the table: "the panel has a chuck clamp" is verified, while
        // "the chuck clamp is M10" is not, and collapsing the two is how someone ends up trusting
        // a guess on real iron.
        // Whether this machine actually has the equipment a code drives. Straight from the manual's
        // own S/O/X columns, read down the base (LTC-208, no suffix) column - the -M/-S/-SM/-MY/-SMY
        // columns are the live-tooling, sub-spindle and Y-axis variants, which this machine is not.
        private enum CodeFitment
        {
            Standard, // "S" - fitted on every base LTC-208.
            Option,   // "O" - the code exists, but only does anything if that option was ordered.
        }

        // Auxiliary M-codes are implemented in the machine BUILDER's PMC ladder, not in the CNC, so
        // a control accepts exactly whatever its own ladder decodes and nothing else. No FANUC
        // manual can answer what these are - only Leadwell's can.
        //
        // *** These are now the REAL codes, transcribed from the LTC-208 operation manual's own
        // M-code list (section 4-1), photographed by the machine's owner on 2026-09-18. The pages
        // are in ReferenceMaterial/Manual/. Everything here was previously an educated guess, and
        // three of those guesses were wrong in ways that would have mattered - see the note on
        // M21/M22 below. ***
        //
        // Codes marked "X" on the base machine are deliberately absent from this table, so they
        // still alarm: they belong to equipment this machine does not have (sub-spindle M59/M60/
        // M110/M111, Cs-axis M90/M91/M96, live tooling M93/M94/M95/M109, Y axis M112/M113,
        // push-bar M16/M76, and essentially everything from M114 up). Running one on this machine
        // would be a programming error, so the simulator treats it as one.
        //
        // The manual's own "SPARE" entries are likewise left out - an unassigned code doing
        // nothing silently is not something to imitate.
        private static readonly Dictionary<int, (string Description, CodeFitment Fitment)> AuxiliaryMCodes = new()
        {
            // --- Workholding and tailstock ---
            [10] = ("Spindle #1 chuck clamp", CodeFitment.Standard),
            [11] = ("Spindle #1 chuck unclamp", CodeFitment.Standard),
            [12] = ("Quill out", CodeFitment.Standard),
            [13] = ("Quill in", CodeFitment.Standard),
            [31] = ("Spindle #1 chuck bypass on", CodeFitment.Standard),
            [32] = ("Spindle #1 chuck bypass off", CodeFitment.Standard),
            [53] = ("Steady clamp", CodeFitment.Option),
            [54] = ("Steady unclamp", CodeFitment.Option),
            [55] = ("Tail stock clamp", CodeFitment.Option),
            [56] = ("Tail stock unclamp", CodeFitment.Option),
            [63] = ("Spindle #1 chuck low pressure mode on", CodeFitment.Option),
            [64] = ("Spindle #1 chuck low pressure mode off", CodeFitment.Option),

            // --- Spindle auxiliaries ---
            // M19/M20 are the orientation pair. M19 is also handled directly in ApplyMCode, since
            // it is one of the few auxiliary codes with an agreed cross-builder meaning.
            [20] = ("Spindle #1 orientation off", CodeFitment.Standard),
            [29] = ("Rigid tapping", CodeFitment.Standard),
            [57] = ("Spindle #1 air blow on", CodeFitment.Option),
            [58] = ("Spindle #1 air blow off", CodeFitment.Option),
            [74] = ("Spindle revolt direction change valid", CodeFitment.Option),
            [75] = ("Spindle revolt restore", CodeFitment.Option),
            [77] = ("Spindle #1 load up set", CodeFitment.Option),
            [78] = ("Spindle #1 load detect off", CodeFitment.Option),
            [79] = ("Spindle #1 load down set", CodeFitment.Option),
            [40] = ("Low gear mode on", CodeFitment.Option),
            [41] = ("High gear mode on", CodeFitment.Option),

            // --- Guarding and interlocks ---
            // NOTE: M21/M22 were guessed as "parts catcher out/in" before the manual turned up.
            // They are the DOOR INTERLOCK BYPASS. That guess was the most dangerous one in the old
            // table: a program written against it would have been bypassing the door interlock
            // while its author believed it was swinging a parts catcher.
            [21] = ("Door interlock bypass on", CodeFitment.Standard),
            [22] = ("Door interlock bypass off", CodeFitment.Standard),
            [17] = ("Auto door close", CodeFitment.Option),
            [18] = ("Auto door open", CodeFitment.Option),

            // --- Part handling ---
            // Parts catcher is M14/M15, not the M21/M22 previously guessed.
            [14] = ("Parts catcher extend", CodeFitment.Option),
            [15] = ("Parts catcher retract", CodeFitment.Option),
            [25] = ("Bar feeder extend", CodeFitment.Option),
            [26] = ("Bar feeder on", CodeFitment.Option),
            [27] = ("Bar feeder off", CodeFitment.Option),
            [28] = ("Load new bar for barfeeder", CodeFitment.Option),
            [50] = ("Robot on", CodeFitment.Option),

            // --- Coolant, swarf and cleaning ---
            // The old table guessed M50/M51 as a wash gun and M52/M53 as the chip conveyor. Both
            // were wrong: the conveyor is M37/M38, and there is no wash-gun code at all - the
            // nearest real codes are M67 chip clean and M68 steady rest clean.
            [7]  = ("Coolant through spindle on", CodeFitment.Option),
            [37] = ("Chip conveyor CW", CodeFitment.Standard),
            [38] = ("Chip conveyor stop", CodeFitment.Standard),
            [67] = ("Chip clean", CodeFitment.Option),
            [68] = ("Steady rest clean", CodeFitment.Option),

            // --- Program and control behaviour ---
            [23] = ("Chamfering on", CodeFitment.Standard),
            [24] = ("Chamfering off", CodeFitment.Standard),
            [33] = ("Block skip on", CodeFitment.Standard),
            [34] = ("Block skip off", CodeFitment.Standard),
            [47] = ("Chuck soft limit 2 unvalid", CodeFitment.Standard),
            [48] = ("Tail stock soft limit 3 unvalid", CodeFitment.Standard),
            [49] = ("Soft limit 2 & 3 valid", CodeFitment.Standard),
            [51] = ("Error detect off", CodeFitment.Standard),
            [52] = ("Error detect on", CodeFitment.Standard),
            [97] = ("Parts counter", CodeFitment.Standard),
            [42] = ("Call macro program (macro type B)", CodeFitment.Option),
            [61] = ("Mirror image X off", CodeFitment.Option),
            [71] = ("Mirror image X on", CodeFitment.Option),
            [65] = ("PMC-axis control on", CodeFitment.Option),
            [66] = ("PMC-axis control off", CodeFitment.Option),

            // --- Tool setter ---
            [43] = ("Setter down", CodeFitment.Option),
            [44] = ("Setter up", CodeFitment.Option),
            [45] = ("Hydraulic tooling-axis I mode on", CodeFitment.Option),
            [46] = ("Hydraulic tooling-axis L mode on", CodeFitment.Option),
        };

        private bool IsAcceptedAuxiliaryMCode(int code)
        {
            if (!AuxiliaryMCodes.TryGetValue(code, out var entry))
                return false;

            // The engine models none of this equipment - there is no chuck state, tailstock quill,
            // conveyor or parts catcher here - so these are accepted and logged, not acted on. Said
            // at the point of use rather than only in the table's comment, so nobody reads a log
            // line as proof the simulator did the thing.
            var note = entry.Fitment == CodeFitment.Option
                ? "manual: option - not modeled here"
                : "manual: standard - not modeled here";
            Messages.Add($"M{code:D2}: {entry.Description} ({note})");
            return true;
        }

        private void ApplySpindleSpeedCommand(GCodeParser.Block block)
        {
            if (!block.Params.TryGetValue("Speed", out var speed))
            {
                if (Modal.Spindle == SpindleMode.ConstantSurfaceSpeed)
                    RecalculateCssSpeed();
                return;
            }

            if (Modal.Spindle == SpindleMode.ConstantSurfaceSpeed)
            {
                Modal.SurfaceSpeedVc = speed;
                RecalculateCssSpeed();
            }
            else
            {
                SpindleSpeed = ClampToMachineMaxRpm(speed);
            }
        }

        // A real spindle cannot be commanded past its maximum: the drive simply runs at the limit.
        // It is not an alarm condition on any control I know of, so this clamps and notes it rather
        // than faulting the program. The limit is this machine's published figure - see MachineSpec.
        private double ClampToMachineMaxRpm(double rpm)
        {
            if (rpm <= MachineSpec.MaxSpindleRpm)
                return rpm;

            Messages.Add($"S{rpm:F0} exceeds the machine maximum - spindle clamped to {MachineSpec.MaxSpindleRpm:F0} RPM");
            return MachineSpec.MaxSpindleRpm;
        }

        // Constant surface speed. The S value under G96 is m/min in metric but surface FEET per
        // minute in inch, and the diameter it divides is held here in mm either way, so the two
        // modes need different constants:
        //   metric  rpm = Vc(m/min) * 1000 / (pi * D_mm)
        //   inch    rpm = Vc(sfm)   *   12 / (pi * D_in), and D_in = D_mm / 25.4,
        //                              which folds to Vc * 304.8 / (pi * D_mm)
        // Using the metric constant in inch mode overstated the speed by 3.28x.
        private void RecalculateCssSpeed()
        {
            var diameterMm = Math.Max(X, 0.1);
            var constant = Modal.Units == UnitsMode.Inch ? 304.8 : 1000.0;
            var rpm = Modal.SurfaceSpeedVc * constant / (Math.PI * diameterMm);
            if (Modal.MaxCssRpm.HasValue)
                rpm = Math.Min(rpm, Modal.MaxCssRpm.Value);
            // The machine's own ceiling applies on top of any programmed G50 clamp, and cannot be
            // programmed away. Under CSS this bites constantly - small diameters ask for enormous
            // rpm - so it is silent here rather than logging on every recalculation.
            SpindleSpeed = Math.Min(rpm, MachineSpec.MaxSpindleRpm);
        }

        private void SelectTool(GCodeParser.Block block)
        {
            if (block.Params.TryGetValue("Tool", out var toolNum))
                CurrentTool = (int)toolNum;

            _activeOffsetNumber = block.Params.TryGetValue("Offset", out var off) ? (int)off : CurrentTool;
            var offset = Offsets.GetOrCreateTool(_activeOffsetNumber);
            _pendingCompLine = null; // new tool may carry a different nose radius - don't miter across the swap

            // The new tool's geometry offset differs from the old one's, so the last tracked render
            // position was computed under a now-stale offset - checking the next rapid against it would
            // be comparing against a position the tool was never actually at, producing a false-positive
            // collision warning. Reuses the same "first rapid of the run is exempt" mechanism (MoveTo's
            // null-check on _lastActualRenderPos) for every tool change, not just the very first move.
            _lastActualRenderPos = null;

            Messages.Add($"T{CurrentTool:D2}{_activeOffsetNumber:D2}: Tool {CurrentTool} selected (offset #{_activeOffsetNumber}, nose R{offset.NoseRadius:F2})");
        }

        private void ApplyDwell(GCodeParser.Block block)
        {
            if (block.Params.TryGetValue("P", out var ms))
            {
                SimulatedSecondsElapsed += ms / 1000.0;
                RecordDwell(ms / 1000.0);
                Spindle.Advance(ms / 1000.0, SpindleTargetRpm);
                Messages.Add($"G04: Dwell {ms:F0} ms");
            }
            else if (block.Params.TryGetValue("X", out var sec))
            {
                SimulatedSecondsElapsed += sec;
                RecordDwell(sec);
                Spindle.Advance(sec, SpindleTargetRpm);
                Messages.Add($"G04: Dwell {sec:F2} sec");
            }
            else
                Messages.Add("G04: Dwell");
        }

        // Resolves a block's target X/Z from X/Z/U/W params, without moving or alarm-checking. Takes
        // an explicit cursor rather than reading X/Z directly so it can also be used as a dry-run
        // (see ExtractContour) that doesn't touch the simulator's real position.
        //
        // G-code system A: X/Z are absolute and U/W are incremental, always - there is no G90/G91
        // modal to consult. Where a block gives both for one axis (e.g. X and U), the incremental
        // address wins, matching how a real control resolves the conflict.
        private (double TargetX, double TargetZ, bool HasMotion) ResolveTargetXZ(double fromX, double fromZ, GCodeParser.Block block)
        {
            var hasX = block.Params.ContainsKey("X");
            var hasZ = block.Params.ContainsKey("Z");
            var hasU = block.Params.ContainsKey("U");
            var hasW = block.Params.ContainsKey("W");

            if (!hasX && !hasZ && !hasU && !hasW)
                return (fromX, fromZ, false);

            var targetX = fromX;
            var targetZ = fromZ;

            if (hasU)
                targetX = fromX + ToMm(block.Params["U"]);
            else if (hasX)
                targetX = ToMm(block.Params["X"]);

            if (hasW)
                targetZ = fromZ + ToMm(block.Params["W"]);
            else if (hasZ)
                targetZ = ToMm(block.Params["Z"]);

            return (targetX, targetZ, true);
        }

        private void ApplyMotion(GCodeParser.Block block)
        {
            var (targetX, targetZ, hasMotion) = ResolveTargetXZ(X, Z, block);
            if (!hasMotion)
                return;

            // A new F-word takes effect for this same block's own move on a real control - must be
            // applied before the move happens, not after, or the cycle-time calculation (and a real
            // machine's actual behavior) would use the previous feed rate for one move too long.
            // RecalculateCssSpeed() deliberately stays AFTER the move below - CSS RPM is meant to
            // reflect the diameter just arrived at (already relied on by the G96 test: a move to a
            // smaller X50 diameter expects RPM recomputed for X50, not the X100 starting point).
            if (block.Params.TryGetValue("Feed", out var feed))
                SetFeedRate(feed);

            if (!TryMoveTo(targetX, targetZ, rapid: Modal.Motion == MotionMode.Rapid))
                return;

            if (Modal.Spindle == SpindleMode.ConstantSurfaceSpeed)
                RecalculateCssSpeed();

            var moveType = Modal.Motion == MotionMode.Rapid ? "G00" : "G01";
            Messages.Add($"{moveType}: X{Len(X)} Z{Len(Z)} @ {Len(FeedRate)} {FeedUnit}");
        }

        // Alarm-checks and moves if in range; returns whether the move happened. Shared by linear
        // moves and every tessellated arc chord, so canned cycles get the same over-travel checking.
        private bool TryMoveTo(double targetX, double targetZ, bool rapid)
        {
            if (targetX > MaxX || targetX < 0)
            {
                Alarms.Add(new Alarm(500, $"X-axis over travel (target {targetX:F2})"));
                return false;
            }
            if (targetZ > MaxZ || targetZ < MinZ)
            {
                Alarms.Add(new Alarm(501, $"Z-axis over travel (target {targetZ:F2})"));
                return false;
            }

            MoveTo(targetX, targetZ, rapid);
            return true;
        }

        private void ApplyArcMotion(GCodeParser.Block block)
        {
            var (targetX, targetZ, hasMotion) = ResolveTargetXZ(X, Z, block);
            if (!hasMotion)
                return;

            var clockwise = Modal.Motion == MotionMode.ArcCw;
            double centerX, centerZ;

            if (block.Params.ContainsKey("I") || block.Params.ContainsKey("K"))
            {
                // I/K are incremental offsets from the arc's start point to its center. I is always a
                // radius value, even with X in diameter, so it counts double against X.
                var i = block.Params.TryGetValue("I", out var iv) ? ToMm(iv) : 0;
                var k = block.Params.TryGetValue("K", out var kv) ? ToMm(kv) : 0;
                centerX = X + 2 * i;
                centerZ = Z + k;
            }
            else if (block.Params.TryGetValue("R", out var rParam))
            {
                var center = ComputeArcCenterFromRadius(X, Z, targetX, targetZ, ToMm(rParam), clockwise);
                if (center == null)
                {
                    Alarms.Add(new Alarm(38, "G02/G03: Invalid radius - start/end points too far apart"));
                    return;
                }
                (centerX, centerZ) = center.Value;
            }
            else
            {
                Alarms.Add(new Alarm(38, "G02/G03: Missing I/K or R (arc center undefined)"));
                return;
            }

            // Same F-word-applies-to-this-block's-own-move fix as ApplyMotion - must happen before
            // the arc actually moves, not after.
            if (block.Params.TryGetValue("Feed", out var feed))
                SetFeedRate(feed);

            // True arc length (radius * sweep angle), not the tessellated chords' summed straight-
            // line distance - close enough to be a near-exact approximation, but "close" isn't
            // "exact", and exact is easy here since the data's already local. Suppress MoveTo's own
            // per-chord time accumulation during tessellation so the two don't double-count.
            // In radius terms - the arc is a true circle only there - and with angles measured the way
            // ComputeArcPoints measures them, so clockwise means the same thing in both.
            var arcRadius = Math.Sqrt(Math.Pow((X - centerX) / 2, 2) + Math.Pow(Z - centerZ, 2));
            var startAngle = ArcAngle((X - centerX) / 2, Z - centerZ);
            var endAngle = ArcAngle((targetX - centerX) / 2, targetZ - centerZ);
            var arcSweep = clockwise ? startAngle - endAngle : endAngle - startAngle;
            while (arcSweep < 0) arcSweep += 2 * Math.PI;
            if (arcSweep < 1e-9) arcSweep = 2 * Math.PI;

            WaitForSpindleArrival();

            var alarmCountBeforeArc = Alarms.Count;
            var firstChordEvent = _timeline?.Events.Count ?? 0;
            // The chords run the spindle forward as they go (so each chord's finish reflects the
            // spindle then); the arc as a whole is then timed from the spindle as it was at its start.
            var spindleAtArcStart = Spindle.ActualRpm;
            _suppressMoveTimeAccumulation = true;
            TessellateArc(X, Z, targetX, targetZ, centerX, centerZ, clockwise);
            _suppressMoveTimeAccumulation = false;
            Spindle.ActualRpm = spindleAtArcStart;
            var arcAlarmed = Alarms.Count != alarmCountBeforeArc;
            var arcSeconds = arcAlarmed ? 0 : MoveSeconds(arcRadius * arcSweep, rapid: false);
            if (!arcAlarmed) // don't charge time for an arc that alarmed out mid-tessellation
                AddMoveTime(arcRadius * arcSweep, rapid: false);

            // The chords were timed by their straight-line length; spread the arc's exact time across
            // them instead, so the timeline and the cycle-time clock agree to the last digit.
            _timeline?.RescaleSince(firstChordEvent, arcSeconds);
            Spindle.Advance(arcSeconds, SpindleTargetRpm);

            if (Modal.Spindle == SpindleMode.ConstantSurfaceSpeed)
                RecalculateCssSpeed();

            var moveType = clockwise ? "G02" : "G03";
            Messages.Add($"{moveType}: X{Len(X)} Z{Len(Z)} @ {Len(FeedRate)} {FeedUnit}");
        }

        // Standard two-circle-intersection: both candidate centers are equidistant (r) from both
        // endpoints; the R-sign convention picks between them (positive R = minor arc <=180deg,
        // negative R = major arc >180deg).
        // Takes and returns X as a diameter, like the rest of the engine, but solves in radius terms:
        // R is a radius of the real circle the tool follows, which is only a circle there.
        private static (double X, double Z)? ComputeArcCenterFromRadius(double x1Dia, double z1, double x2Dia, double z2, double r, bool clockwise)
        {
            var center = ComputeArcCenterInRadiusSpace(x1Dia / 2, z1, x2Dia / 2, z2, r, clockwise);
            return center.HasValue ? (center.Value.X * 2, center.Value.Z) : null;
        }

        private static (double X, double Z)? ComputeArcCenterInRadiusSpace(double x1, double z1, double x2, double z2, double r, bool clockwise)
        {
            var dx = x2 - x1;
            var dz = z2 - z1;
            var d = Math.Sqrt(dx * dx + dz * dz);
            if (d < 1e-9)
                return null;

            var absR = Math.Abs(r);
            if (absR < d / 2 - 1e-6)
                return null; // radius too small to reach both points

            var h = Math.Sqrt(Math.Max(0, absR * absR - (d / 2) * (d / 2)));
            var mx = (x1 + x2) / 2;
            var mz = (z1 + z2) / 2;
            var perpX = -dz / d;
            var perpZ = dx / d;

            var c1 = (X: mx + h * perpX, Z: mz + h * perpZ);
            var c2 = (X: mx - h * perpX, Z: mz - h * perpZ);

            bool IsMajorArc(double cx, double cz)
            {
                var a1 = ArcAngle(x1 - cx, z1 - cz);
                var a2 = ArcAngle(x2 - cx, z2 - cz);
                var sweep = clockwise ? a1 - a2 : a2 - a1;
                while (sweep < 0) sweep += 2 * Math.PI;
                return sweep > Math.PI;
            }

            var wantMajor = r < 0;
            return IsMajorArc(c1.X, c1.Z) == wantMajor ? c1 : c2;
        }

        // Renders the arc as short chords through the existing TryMoveTo path, so it automatically
        // gets tool-offset, cutter comp, corner mitering, and over-travel checking for free.
        private void TessellateArc(double startX, double startZ, double endX, double endZ, double centerX, double centerZ, bool clockwise)
        {
            foreach (var (px, pz) in ComputeArcPoints(startX, startZ, endX, endZ, centerX, centerZ, clockwise))
            {
                if (!TryMoveTo(px, pz, rapid: false))
                    return; // alarm already logged - stop tessellating further chords
            }
        }

        // Angle of a point about an arc's center, in radius terms, measured from +Z toward +X: the
        // usual counter-clockwise angle on a lathe drawing (Z to the right, X up), which is the view
        // G02 (clockwise) and G03 (counter-clockwise) are defined in - so a convex corner turned from
        // the face toward the chuck is a G03, the textbook case. Measuring from +X toward +Z instead,
        // as this engine once did, silently swapped the two.
        private static double ArcAngle(double radial, double axial) => Math.Atan2(radial, axial);

        // Pure point generation for an arc, shared by TessellateArc (real motion) and
        // ExtractContour (dry-run canned-cycle contour capture, so arcs embedded in a G71/G72/G70
        // profile get walked as the actual curve rather than degrading to a straight chord).
        // X in and out is a diameter; the circle itself is walked in radius terms, since that is the
        // only space it is a circle in (in diameter terms it would be an ellipse twice as tall).
        private static List<(double X, double Z)> ComputeArcPoints(double startX, double startZ, double endX, double endZ, double centerX, double centerZ, bool clockwise)
        {
            var points = new List<(double X, double Z)>();

            var startR = startX / 2;
            var endR = endX / 2;
            var centerR = centerX / 2;

            var radius = Math.Sqrt(Math.Pow(startR - centerR, 2) + Math.Pow(startZ - centerZ, 2));
            if (radius < 1e-6)
                return points;

            var startAngle = ArcAngle(startR - centerR, startZ - centerZ);
            var endAngle = ArcAngle(endR - centerR, endZ - centerZ);

            var sweep = clockwise ? startAngle - endAngle : endAngle - startAngle;
            while (sweep < 0) sweep += 2 * Math.PI;
            if (sweep < 1e-9) sweep = 2 * Math.PI; // coincident start/end = full circle

            var segments = Math.Max(4, (int)Math.Ceiling(sweep / (Math.PI / 60))); // ~3 degrees/segment
            var angleStep = sweep / segments * (clockwise ? -1 : 1);
            var currentAngle = startAngle;

            for (int s = 1; s <= segments; s++)
            {
                if (s == segments)
                {
                    points.Add((endX, endZ)); // land exactly on the programmed endpoint, avoid float drift
                }
                else
                {
                    currentAngle += angleStep;
                    points.Add((2 * (centerR + radius * Math.Sin(currentAngle)), centerZ + radius * Math.Cos(currentAngle)));
                }
            }

            return points;
        }

        private void MoveTo(double targetX, double targetZ, bool rapid)
        {
            // Arcs do their own wait before their first chord (see ApplyArcMotion).
            if (!rapid && !_suppressMoveTimeAccumulation)
                WaitForSpindleArrival();

            var offset = Offsets.GetOrCreateTool(_activeOffsetNumber);

            // Turning and boring are the cuts whose finish is modelled: a round nose dragged along
            // the part. Drilling, grooving and threading leave surfaces this doesn't estimate.
            var trackFinish = !rapid && offset.Type is ToolType.OdTurning or ToolType.IdBoring;

            // Every threading cycle requires a threading tool, so the tool tells a thread cut apart.
            var feedOverrideApplies = !rapid && offset.Type != ToolType.Threading;

            // Said once per ramp, before this move's own log, so playback shows it as the cut starts.
            var target = SpindleTargetRpm;
            if (trackFinish && target != 0 && Math.Abs(Spindle.ActualRpm) < Math.Abs(target) * 0.95
                && _rampNoticeTarget != target)
            {
                Messages.Add($"Cutting before the spindle is up to speed: {Math.Abs(Spindle.ActualRpm):F0} of {Math.Abs(target):F0} RPM - rough finish");
                _rampNoticeTarget = target;
            }

            // Before anything this move itself logs (a collision warning, say), so playback shows
            // that warning when the move is reached rather than one move early.
            var (messagesBefore, warningsBefore, alarmsBefore) = (Messages.Count, Warnings.Count, Alarms.Count);

            // X is a diameter, but the tool only travels half of any change in it. Lengths, directions
            // and the comp offset below are all worked out in radius terms, the space the tool really
            // moves in; doing them on the diameter doubled X travel and skewed every angle.
            var drx = (targetX - X) / 2;
            var dz = targetZ - Z;
            var len = Math.Sqrt(drx * drx + dz * dz);
            var hasDirection = len > 1e-6;

            // A feed runs at F along the path. A rapid runs each axis at up to its own rapid rate, so
            // it takes as long as the longer axis needs - true for straight and dogleg rapids alike.
            var travel = rapid ? Math.Max(Math.Abs(drx), Math.Abs(dz)) : len;
            var plan = PlanMove(travel, rapid, trackFinish, offset.NoseRadius, feedOverrideApplies);
            if (!_suppressMoveTimeAccumulation)
                SimulatedSecondsElapsed += plan.Seconds;
            var ndx = hasDirection ? drx / len : 0;
            var ndz = hasDirection ? dz / len : 0;

            // True vector comp: offset perpendicular to this segment's own direction of travel,
            // rather than a constant X-only shift - so angled cuts compensate correctly too.
            // Assumes a front tool post; doesn't model the 9 imaginary tool-nose-direction vectors
            // real controls use for rear-mounted or unusual tool orientations.
            var compActive = Modal.Comp != CutterComp.Off && offset.NoseRadius != 0 && hasDirection;
            var (compRx, compDz) = compActive ? ComputeCompOffset(ndx, ndz, offset.NoseRadius, Modal.Comp) : (0, 0);
            var compDx = compRx * 2; // back to a diameter, like everything else in X

            // X/Z (and the target passed in) are work coordinates in the currently active work
            // offset's frame - the stock itself sits at a fixed machine-space location, so the
            // render/carve/collision position needs the active work offset folded in too, exactly
            // like the tool's own geometry+wear offset just below. Without this, switching G54/G55/
            // etc with different stored X/Z would carve at the wrong physical location (invisible
            // until now since every work offset defaults to X0 Z0, a no-op either way).
            var workOffset = Offsets.WorkOffsets.TryGetValue(Modal.ActiveWorkOffset, out var wo) ? wo : null;
            var workOffsetX = workOffset?.X ?? 0;
            var workOffsetZ = workOffset?.Z ?? 0;

            var fromRenderX = X + offset.TotalX + compDx + workOffsetX;
            var fromRenderZ = Z + offset.TotalZ + compDz + workOffsetZ;
            var toRenderX = targetX + offset.TotalX + compDx + workOffsetX;
            var toRenderZ = targetZ + offset.TotalZ + compDz + workOffsetZ;

            // Miter this segment's corner against the previous comp-active segment by intersecting
            // their offset lines, instead of leaving each segment's independently-offset endpoints
            // unconnected. A rapid breaks the chain (real cutter comp only blends between successive
            // cutting moves), and an extreme direction reversal falls back to the plain offset point
            // rather than a huge miter spike (no arc insertion for reflex corners).
            if (compActive && !rapid && _pendingCompLine.HasValue)
            {
                var (px, pz, pdx, pdz) = _pendingCompLine.Value;
                // Intersected in radius terms, like the directions; the corner goes back to a diameter.
                var corner = IntersectLines(px / 2, pz, pdx, pdz, fromRenderX / 2, fromRenderZ, ndx, ndz);
                if (corner.HasValue)
                {
                    var cornerX = corner.Value.X * 2;
                    var cornerZ = corner.Value.Z;
                    var spike = Math.Sqrt(Math.Pow((cornerX - fromRenderX) / 2, 2) + Math.Pow(cornerZ - fromRenderZ, 2));
                    if (spike <= offset.NoseRadius * 5)
                    {
                        var lastIndex = ToolPath.Count - 1;
                        if (lastIndex >= 0)
                            ToolPath[lastIndex] = (cornerX, cornerZ, ToolPath[lastIndex].Type);

                        // Keep the playback timeline's drawing in step with ToolPath. Only the drawn
                        // end point moves - that segment's carve already happened un-mitered.
                        if (_lastMotionEvent != null)
                        {
                            _lastMotionEvent.ToRenderX = cornerX;
                            _lastMotionEvent.ToRenderZ = cornerZ;
                        }

                        fromRenderX = cornerX;
                        fromRenderZ = cornerZ;
                    }
                }
            }

            ToolPath.Add((fromRenderX, fromRenderZ, rapid ? "rapid" : "feed"));
            ToolPath.Add((toRenderX, toRenderZ, rapid ? "rapid" : "feed"));

            if (rapid)
            {
                // A rapid traverse into remaining stock is a crash on a real machine. Anchor the
                // check's start to where the tool actually last was (see _lastActualRenderPos) rather
                // than this segment's own from-point, so a tool change's offset jump can't mask a real
                // commanded move into the part; only skipped before the very first move of the run.
                var (checkFromX, checkFromZ) = _lastActualRenderPos ?? (fromRenderX, fromRenderZ);
                if (_lastActualRenderPos.HasValue && Stock.IntersectsMaterial(checkFromZ, checkFromX, toRenderZ, toRenderX))
                {
                    // Not a machine alarm - a real control has no way to know this happened. Goes to
                    // Warnings (message log only), never Alarms (ALARM screen / ALM badge).
                    Warnings.Add(
                        $"COLLISION WARNING: G00 rapid traverse - T{CurrentTool:D2} path from X{checkFromX:F2} Z{checkFromZ:F2} to X{toRenderX:F2} Z{toRenderZ:F2} cuts through remaining stock");
                    ToolPath[^2] = (ToolPath[^2].X, ToolPath[^2].Z, "collision");
                    ToolPath[^1] = (ToolPath[^1].X, ToolPath[^1].Z, "collision");
                }
            }
            // What this segment carves, recorded exactly as passed to StockProfile so playback can
            // replay it faithfully.
            var carve = CarveKind.None;
            double carveZ1 = 0, carveX1 = 0, carveZ2 = 0, carveX2 = 0;
            if (!rapid)
            {
                // Carve the stock profile along the same segment just rendered. An Undefined-type
                // active tool has no known geometry to carve with (same "don't guess" stance as the
                // canned-cycle tool-type validation) and is silently skipped.
                switch (offset.Type)
                {
                    case ToolType.OdTurning:
                    case ToolType.Grooving:
                    case ToolType.Threading:
                        (carve, carveZ1, carveX1, carveZ2, carveX2) = (CarveKind.Outer, fromRenderZ, fromRenderX, toRenderZ, toRenderX);
                        break;
                    case ToolType.IdBoring:
                    case ToolType.IdGrooving:
                        (carve, carveZ1, carveX1, carveZ2, carveX2) = (CarveKind.Inner, fromRenderZ, fromRenderX, toRenderZ, toRenderX);
                        break;
                    case ToolType.Drill:
                        // A drill's hole diameter is the tool's fixed geometry, not the programmed X
                        // (which is typically 0 - the drill tip's centerline travel).
                        (carve, carveZ1, carveX1, carveZ2, carveX2) = (CarveKind.Inner, fromRenderZ, offset.Width, toRenderZ, offset.Width);
                        break;
                }

            }

            // One piece normally; several while the spindle is still ramping, so the finish can
            // change along the cut. Each piece carves its own stretch and is its own timeline event;
            // only the cut's real ends reach past into the next sample (see StockProfile.Carve).
            static double Lerp(double a, double b, double f) => f >= 1 ? b : a + (b - a) * f;
            for (int k = 0; k < plan.Pieces.Count; k++)
            {
                var piece = plan.Pieces[k];
                var (f0, f1) = (piece.FromFraction, piece.ToFraction);
                var first = k == 0;
                var last = k == plan.Pieces.Count - 1;
                var (pz1, px1) = (Lerp(carveZ1, carveZ2, f0), Lerp(carveX1, carveX2, f0));
                var (pz2, px2) = (Lerp(carveZ1, carveZ2, f1), Lerp(carveX1, carveX2, f1));
                if (first)
                    (pz1, px1) = (carveZ1, carveX1);

                if (carve == CarveKind.Outer)
                    Stock.CarveOuter(pz1, px1, pz2, px2, piece.Ra, first, last);
                else if (carve == CarveKind.Inner)
                    Stock.CarveInner(pz1, px1, pz2, px2, piece.Ra, first, last);

                if (_timeline == null)
                    continue;

                // Inside an arc, the chords' time is provisional - ApplyArcMotion rescales them so
                // they share the arc's exact duration, the same number the cycle-time clock gets.
                var ev = new TimelineEvent
                {
                    Seq = _timeline.NextSeq(),
                    StartTime = _timeline.Duration,
                    Duration = piece.Seconds,
                    Kind = !rapid ? TimelineEventKind.Feed
                        : ToolPath[^1].Type == "collision" ? TimelineEventKind.Collision
                        : TimelineEventKind.Rapid,
                    FromRenderX = first ? fromRenderX : Lerp(fromRenderX, toRenderX, f0),
                    FromRenderZ = first ? fromRenderZ : Lerp(fromRenderZ, toRenderZ, f0),
                    ToRenderX = Lerp(fromRenderX, toRenderX, f1),
                    ToRenderZ = Lerp(fromRenderZ, toRenderZ, f1),
                    FromX = first ? X : Lerp(X, targetX, f0),
                    FromZ = first ? Z : Lerp(Z, targetZ, f0),
                    ToX = Lerp(X, targetX, f1),
                    ToZ = Lerp(Z, targetZ, f1),
                    Carve = carve,
                    CarveZ1 = pz1, CarveX1 = px1, CarveZ2 = pz2, CarveX2 = px2,
                    CarveRa = piece.Ra,
                    CarveReachStart = first,
                    CarveReachEnd = last,
                    FeedOverrideApplies = feedOverrideApplies,
                    Line = _currentLine,
                    State = CaptureState(piece.StartRpm),
                    MessagesBefore = first ? messagesBefore : Messages.Count,
                    WarningsBefore = first ? warningsBefore : Warnings.Count,
                    AlarmsBefore = first ? alarmsBefore : Alarms.Count,
                };
                _timeline.Events.Add(ev);
                _timeline.Duration += piece.Seconds;
                _lastMotionEvent = ev;
            }

            // The spindle keeps turning (and ramping) through every move, rapids included.
            Spindle.Advance(plan.Seconds, target);

            _lastActualRenderPos = (toRenderX, toRenderZ);

            _pendingCompLine = (compActive && !rapid) ? (fromRenderX, fromRenderZ, ndx, ndz) : null;

            X = targetX;
            Z = targetZ;
        }

        private static (double Dx, double Dz) ComputeCompOffset(double ndx, double ndz, double radius, CutterComp comp) =>
            comp == CutterComp.Left ? (-ndz * radius, ndx * radius) : (ndz * radius, -ndx * radius);

        // Intersection of two infinite 2D lines, each given as a point + direction vector.
        private static (double X, double Z)? IntersectLines(double x1, double z1, double dx1, double dz1, double x2, double z2, double dx2, double dz2)
        {
            var denom = dx1 * dz2 - dz1 * dx2;
            if (Math.Abs(denom) < 1e-9)
                return null; // parallel/collinear - already lines up, no correction needed

            var t = ((x2 - x1) * dz2 - (z2 - z1) * dx2) / denom;
            return (x1 + t * dx1, z1 + t * dz1);
        }

        // Public so the UI can convert operator-entered values (stock size) the same way the engine
        // converts programmed ones, rather than keeping a second copy of the 25.4 constant.
        public double ToMm(double value) => Modal.Units == UnitsMode.Inch ? value * InchToMm : value;

        // Converts mm back to whatever the active unit is - for display, and for log strings that
        // must echo the number the operator actually programmed rather than the internal one.
        public double FromMm(double valueMm) => Modal.Units == UnitsMode.Inch ? valueMm / InchToMm : valueMm;

        // Every F word goes through here. FeedRate is held in mm (per minute or per rev) to match
        // X/Z, so an inch-mode F has to be converted on the way in exactly like a coordinate does -
        // it wasn't, which was harmless only for as long as metric was the power-on default. A
        // threading lead is an F word too and converts identically.
        private void SetFeedRate(double commanded) => FeedRate = ToMm(commanded);

        // Operator-visible log strings must echo the active unit, not the mm the engine happens to
        // store - a message reading "retract 0.50mm" against a program that asked for R.02 in inch
        // is actively misleading about what the control did.
        private string LenUnit => Modal.Units == UnitsMode.Inch ? "in" : "mm";
        private string Len(double valueMm) => FromMm(valueMm).ToString(Modal.Units == UnitsMode.Inch ? "F4" : "F3");
        private string FeedUnit => Modal.Units == UnitsMode.Inch
            ? (Modal.Feed == FeedMode.PerRevolution ? "in/rev" : "in/min")
            : (Modal.Feed == FeedMode.PerRevolution ? "mm/rev" : "mm/min");
    }
}
