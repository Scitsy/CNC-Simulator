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
        public double FeedRate { get; set; } = 100;
        public int CurrentTool { get; set; } = 0;
        public bool CoolantOn { get; set; } = false;

        public ModalState Modal { get; } = new();
        public OffsetTables Offsets { get; }

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
        public double StockLength { get; set; } = 100;    // mm
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

        private enum BlockRangeExit { RanOffEnd, Returned, Paused, ProgramEnded }

        public RunResult RunProgram(List<GCodeParser.Block> blocks, int startIndex = 0)
        {
            Messages.Clear();
            Alarms.Clear();
            Warnings.Clear();

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
                    continue;
                }

                if (IsDrillingSetup(toExecute))
                {
                    StoreDrillingSetup(toExecute);
                    i++;
                    continue;
                }

                if (IsGroovingSetup(toExecute))
                {
                    StoreGroovingSetup(toExecute);
                    i++;
                    continue;
                }

                if (IsThreadingSetup(toExecute))
                {
                    StoreThreadingSetup(toExecute);
                    i++;
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
                    continue;
                }

                TriggerModalMacroIfArmed(toExecute, blocks);
                ExecuteBlock(toExecute);

                if (isTopLevel && toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 0))
                    return (BlockRangeExit.Paused, i + 1);

                if (isTopLevel && toExecute.Commands.Any(c => c.Type == 'M' && c.Code == 30))
                    return (BlockRangeExit.ProgramEnded, 0);

                i++;
            }

            if (!isTopLevel)
                Messages.Add("WARNING: Subprogram/macro ran off end without M99");
            return (BlockRangeExit.RanOffEnd, i);
        }

        private void ExecuteBlock(GCodeParser.Block block)
        {
            foreach (var (_, code) in block.Commands.Where(c => c.Type == 'G'))
                ApplyGCode(code, block);

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
                MoveTo(0, 0, rapid: true);
                Messages.Add("G28: Return to reference position");
                return;
            }

            if (block.Commands.Any(c => c.Type == 'G' && c.Code == 50) &&
                (block.Params.ContainsKey("X") || block.Params.ContainsKey("Z")))
            {
                if (block.Params.TryGetValue("X", out var presetX)) X = ToMm(presetX);
                if (block.Params.TryGetValue("Z", out var presetZ)) Z = ToMm(presetZ);
                Messages.Add($"G50: Coordinate preset X{X:F2} Z{Z:F2}");
                return;
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
                case 90:
                    Modal.Position = PositionMode.Absolute;
                    Messages.Add("G90: Absolute positioning");
                    break;
                case 91:
                    Modal.Position = PositionMode.Incremental;
                    Messages.Add("G91: Incremental positioning");
                    break;
                case 96:
                    Modal.Spindle = SpindleMode.ConstantSurfaceSpeed;
                    if (block.Params.TryGetValue("Speed", out var vc))
                        Modal.SurfaceSpeedVc = vc;
                    Messages.Add($"G96: Constant surface speed @ {Modal.SurfaceSpeedVc:F0} m/min");
                    RecalculateCssSpeed();
                    break;
                case 97:
                    Modal.Spindle = SpindleMode.ConstantRpm;
                    if (block.Params.TryGetValue("Speed", out var rpm))
                        SpindleSpeed = rpm;
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
            }
        }

        private void ApplyMCode(int code, GCodeParser.Block block)
        {
            switch (code)
            {
                case 0:
                    Messages.Add("M00: Program stop");
                    break;
                case 1:
                    Messages.Add("M01: Optional stop (ignored - switch not modeled)");
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
                case 30:
                    Messages.Add("M30: Program end, rewind");
                    break;
                case 99:
                    Messages.Add("M99: Return from subprogram");
                    break;
            }
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
                SpindleSpeed = speed;
            }
        }

        private void RecalculateCssSpeed()
        {
            var diameter = Math.Max(X, 0.1);
            var rpm = Modal.SurfaceSpeedVc * 1000 / (Math.PI * diameter);
            if (Modal.MaxCssRpm.HasValue)
                rpm = Math.Min(rpm, Modal.MaxCssRpm.Value);
            SpindleSpeed = rpm;
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
                Messages.Add($"G04: Dwell {ms:F0} ms");
            else if (block.Params.TryGetValue("X", out var sec))
                Messages.Add($"G04: Dwell {sec:F2} sec");
            else
                Messages.Add("G04: Dwell");
        }

        // Resolves a block's target X/Z from X/Z/U/W params (respecting G90/G91 and the U/W-always-
        // incremental rule), without moving or alarm-checking. Takes an explicit cursor rather than
        // reading X/Z directly so it can also be used as a dry-run (see ExtractContour) that doesn't
        // touch the simulator's real position.
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
                targetX = Modal.Position == PositionMode.Incremental ? fromX + ToMm(block.Params["X"]) : ToMm(block.Params["X"]);

            if (hasW)
                targetZ = fromZ + ToMm(block.Params["W"]);
            else if (hasZ)
                targetZ = Modal.Position == PositionMode.Incremental ? fromZ + ToMm(block.Params["Z"]) : ToMm(block.Params["Z"]);

            return (targetX, targetZ, true);
        }

        private void ApplyMotion(GCodeParser.Block block)
        {
            var (targetX, targetZ, hasMotion) = ResolveTargetXZ(X, Z, block);
            if (!hasMotion || !TryMoveTo(targetX, targetZ, rapid: Modal.Motion == MotionMode.Rapid))
                return;

            if (block.Params.TryGetValue("Feed", out var feed))
                FeedRate = feed;

            if (Modal.Spindle == SpindleMode.ConstantSurfaceSpeed)
                RecalculateCssSpeed();

            var feedLabel = Modal.Feed == FeedMode.PerRevolution ? "mm/rev" : "mm/min";
            var moveType = Modal.Motion == MotionMode.Rapid ? "G00" : "G01";
            Messages.Add($"{moveType}: X{X:F2} Z{Z:F2} @ {FeedRate:F2}{feedLabel}");
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
                // I/K are incremental offsets from the arc's start point to its center.
                var i = block.Params.TryGetValue("I", out var iv) ? ToMm(iv) : 0;
                var k = block.Params.TryGetValue("K", out var kv) ? ToMm(kv) : 0;
                centerX = X + i;
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

            TessellateArc(X, Z, targetX, targetZ, centerX, centerZ, clockwise);

            if (block.Params.TryGetValue("Feed", out var feed))
                FeedRate = feed;
            if (Modal.Spindle == SpindleMode.ConstantSurfaceSpeed)
                RecalculateCssSpeed();

            var feedLabel = Modal.Feed == FeedMode.PerRevolution ? "mm/rev" : "mm/min";
            var moveType = clockwise ? "G02" : "G03";
            Messages.Add($"{moveType}: X{X:F2} Z{Z:F2} @ {FeedRate:F2}{feedLabel}");
        }

        // Standard two-circle-intersection: both candidate centers are equidistant (r) from both
        // endpoints; the R-sign convention picks between them (positive R = minor arc <=180deg,
        // negative R = major arc >180deg).
        private static (double X, double Z)? ComputeArcCenterFromRadius(double x1, double z1, double x2, double z2, double r, bool clockwise)
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
                var a1 = Math.Atan2(z1 - cz, x1 - cx);
                var a2 = Math.Atan2(z2 - cz, x2 - cx);
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

        // Pure point generation for an arc, shared by TessellateArc (real motion) and
        // ExtractContour (dry-run canned-cycle contour capture, so arcs embedded in a G71/G72/G70
        // profile get walked as the actual curve rather than degrading to a straight chord).
        private static List<(double X, double Z)> ComputeArcPoints(double startX, double startZ, double endX, double endZ, double centerX, double centerZ, bool clockwise)
        {
            var points = new List<(double X, double Z)>();

            var radius = Math.Sqrt(Math.Pow(startX - centerX, 2) + Math.Pow(startZ - centerZ, 2));
            if (radius < 1e-6)
                return points;

            var startAngle = Math.Atan2(startZ - centerZ, startX - centerX);
            var endAngle = Math.Atan2(endZ - centerZ, endX - centerX);

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
                    points.Add((centerX + radius * Math.Cos(currentAngle), centerZ + radius * Math.Sin(currentAngle)));
                }
            }

            return points;
        }

        private void MoveTo(double targetX, double targetZ, bool rapid)
        {
            var offset = Offsets.GetOrCreateTool(_activeOffsetNumber);

            var dx = targetX - X;
            var dz = targetZ - Z;
            var len = Math.Sqrt(dx * dx + dz * dz);
            var hasDirection = len > 1e-6;
            var ndx = hasDirection ? dx / len : 0;
            var ndz = hasDirection ? dz / len : 0;

            // True vector comp: offset perpendicular to this segment's own direction of travel,
            // rather than a constant X-only shift - so angled cuts compensate correctly too.
            // Assumes a front tool post; doesn't model the 9 imaginary tool-nose-direction vectors
            // real controls use for rear-mounted or unusual tool orientations.
            var compActive = Modal.Comp != CutterComp.Off && offset.NoseRadius != 0 && hasDirection;
            var (compDx, compDz) = compActive ? ComputeCompOffset(ndx, ndz, offset.NoseRadius, Modal.Comp) : (0, 0);

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
                var corner = IntersectLines(px, pz, pdx, pdz, fromRenderX, fromRenderZ, ndx, ndz);
                if (corner.HasValue)
                {
                    var spike = Math.Sqrt(Math.Pow(corner.Value.X - fromRenderX, 2) + Math.Pow(corner.Value.Z - fromRenderZ, 2));
                    if (spike <= offset.NoseRadius * 5)
                    {
                        var lastIndex = ToolPath.Count - 1;
                        if (lastIndex >= 0)
                            ToolPath[lastIndex] = (corner.Value.X, corner.Value.Z, ToolPath[lastIndex].Type);

                        fromRenderX = corner.Value.X;
                        fromRenderZ = corner.Value.Z;
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
            else
            {
                // Carve the stock profile along the same segment just rendered. An Undefined-type
                // active tool has no known geometry to carve with (same "don't guess" stance as the
                // canned-cycle tool-type validation) and is silently skipped.
                switch (offset.Type)
                {
                    case ToolType.OdTurning:
                    case ToolType.Grooving:
                    case ToolType.Threading:
                        Stock.CarveOuter(fromRenderZ, fromRenderX, toRenderZ, toRenderX);
                        break;
                    case ToolType.IdBoring:
                    case ToolType.IdGrooving:
                        Stock.CarveInner(fromRenderZ, fromRenderX, toRenderZ, toRenderX);
                        break;
                    case ToolType.Drill:
                        // A drill's hole diameter is the tool's fixed geometry, not the programmed X
                        // (which is typically 0 - the drill tip's centerline travel).
                        Stock.CarveInner(fromRenderZ, offset.Width, toRenderZ, offset.Width);
                        break;
                }
            }
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

        private double ToMm(double value) => Modal.Units == UnitsMode.Inch ? value * InchToMm : value;
    }
}
