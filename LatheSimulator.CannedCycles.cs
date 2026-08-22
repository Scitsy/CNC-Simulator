using System;
using System.Collections.Generic;
using System.Linq;

namespace FanucSimulator
{
    public partial class LatheSimulator
    {
        private double _roughingDepth = 0;
        private double _roughingRetract = 1.0;
        private double _groovingRetract = 0.5;
        private int _threadFinishPasses = 1;
        private double _threadTipAngle = 60;
        private double _threadMinDepthOfCut = 0.02;

        private static bool IsRoughingSetup(GCodeParser.Block block) =>
            block.Commands.Any(c => c.Type == 'G' && (c.Code == 71 || c.Code == 72)) && !block.Params.ContainsKey("P");

        private static bool IsGroovingSetup(GCodeParser.Block block) =>
            block.Commands.Any(c => c.Type == 'G' && c.Code == 75) && !block.Params.ContainsKey("X");

        private void StoreGroovingSetup(GCodeParser.Block block)
        {
            _groovingRetract = block.Params.TryGetValue("R", out var r) ? Math.Abs(ToMm(r)) : 0.5;
            Messages.Add($"G75: Grooving cycle setup, retract {_groovingRetract:F2}mm");
        }

        private static bool IsThreadingSetup(GCodeParser.Block block) =>
            block.Commands.Any(c => c.Type == 'G' && c.Code == 76) && !block.Params.ContainsKey("X");

        // Setup P is a compound encoded value P(m)(r)(a): m = finishing pass count (2 digits),
        // r = chamfer amount in 0.1*lead units (2 digits, unused in this simplified model), a = tool
        // tip angle in degrees (2 digits). E.g. P021060 -> m=02, r=10, a=60.
        private void StoreThreadingSetup(GCodeParser.Block block)
        {
            if (block.Params.TryGetValue("P", out var pRaw))
            {
                var raw = (int)pRaw;
                _threadTipAngle = raw % 100;
                raw /= 100;
                raw /= 100; // chamfer digits - not modeled, thread end is square
                _threadFinishPasses = Math.Max(1, raw);
            }
            if (block.Params.TryGetValue("Q", out var qRaw))
                _threadMinDepthOfCut = Math.Max(0.005, Math.Abs(qRaw) / 1000.0);

            Messages.Add($"G76: Threading cycle setup, {_threadFinishPasses} finish pass(es), {_threadTipAngle:F0}deg tip, min depth {_threadMinDepthOfCut:F3}mm");
        }

        private static bool IsCannedCycleTrigger(GCodeParser.Block block, out int code)
        {
            foreach (var (type, c) in block.Commands)
            {
                if (type != 'G') continue;

                if ((c == 70 || c == 71 || c == 72) && block.Params.ContainsKey("P") && block.Params.ContainsKey("Q"))
                { code = c; return true; }

                if (c == 75 && block.Params.ContainsKey("X") && block.Params.ContainsKey("Z"))
                { code = c; return true; }

                if (c == 76 && block.Params.ContainsKey("X") && block.Params.ContainsKey("Z") && block.Params.ContainsKey("Q"))
                { code = c; return true; }
            }
            code = 0;
            return false;
        }

        private void StoreRoughingSetup(GCodeParser.Block block)
        {
            // G71's setup block gives depth as U (stepping axis = X); G72's gives it as W (stepping
            // axis = Z) - only one is present in a valid setup block, so just use whichever is there.
            if (block.Params.TryGetValue("U", out var depthU))
                _roughingDepth = Math.Max(0.05, Math.Abs(ToMm(depthU)));
            else if (block.Params.TryGetValue("W", out var depthW))
                _roughingDepth = Math.Max(0.05, Math.Abs(ToMm(depthW)));

            _roughingRetract = block.Params.TryGetValue("R", out var retract) ? Math.Abs(ToMm(retract)) : 0.5;

            var code = block.Commands.First(c => c.Type == 'G' && (c.Code == 71 || c.Code == 72)).Code;
            Messages.Add($"G{code}: Roughing cycle setup, depth {_roughingDepth:F2}mm/pass, retract {_roughingRetract:F2}mm");
        }

        private void ExecuteCannedCycleTrigger(int code, GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            switch (code)
            {
                case 70: ExecuteFinishingCycle(block, blocks); break;
                case 71: ExecuteRoughingCycle(isFacing: false, block, blocks); break;
                case 72: ExecuteRoughingCycle(isFacing: true, block, blocks); break;
                case 75: ExecuteGroovingCycle(block); break;
                case 76: ExecuteThreadingCycle(block); break;
            }
        }

        // Fool-proofing: catches the real, common mistake of calling a canned cycle with the wrong
        // kind of tool selected (e.g. running G76 threading with a plain OD turning tool). Doesn't
        // attempt to validate plain G0/G1 moves (like drilling) - that's ambiguous and would risk
        // false positives on legitimate programs, so only the unambiguous canned-cycle cases here
        // get checked.
        private bool CheckToolType(int code, string cycleName, params ToolType[] allowed)
        {
            var tool = Offsets.GetOrCreateTool(_activeOffsetNumber);
            if (allowed.Contains(tool.Type))
                return true;

            var allowedNames = string.Join(" or ", allowed);
            Alarms.Add(new Alarm(85, $"G{code}: Wrong tool type for {cycleName} - T{CurrentTool:D2} is {tool.Type}, expected {allowedNames}"));
            return false;
        }

        private void ExecuteThreadingCycle(GCodeParser.Block block)
        {
            if (!CheckToolType(76, "threading", ToolType.Threading))
                return;

            var (targetX, targetZ, hasMotion) = ResolveTargetXZ(X, Z, block);
            if (!hasMotion)
            {
                Alarms.Add(new Alarm(79, "G76: Missing X/Z target"));
                return;
            }

            // Radial infeed only - doesn't model compound-angle (flank) infeed direction.
            var taper = block.Params.TryGetValue("R", out var rVal) ? ToMm(rVal) : 0;
            var threadHeight = block.Params.TryGetValue("P", out var pVal) ? Math.Abs(pVal) / 1000.0 : 0;
            var firstCutDepth = block.Params.TryGetValue("Q", out var qVal) ? Math.Abs(qVal) / 1000.0 : 0.1;
            var lead = block.Params.TryGetValue("Feed", out var f) ? f : FeedRate;

            if (threadHeight <= 1e-9)
            {
                Messages.Add("G76: Thread height (P) is zero - nothing to cut");
                return;
            }

            var startX = X;
            var startZ = Z;
            var depths = BuildThreadDepthSchedule(threadHeight, firstCutDepth, _threadMinDepthOfCut, _threadFinishPasses);

            Messages.Add($"G76: Threading cycle, {depths.Count} pass(es), thread height {threadHeight:F3}mm, lead {lead:F3}mm/rev");

            FeedRate = lead;
            for (int p = 0; p < depths.Count; p++)
            {
                var depth = depths[p];
                var passStartX = startX - 2 * depth;
                var passEndX = passStartX - 2 * taper;

                // Reposition to startZ at the fully-clear startX first (single-axis rapid, matches
                // the safe retract already used below), then engage the pass depth at the now-fixed
                // startZ. That infeed is a FEED move, not a rapid - it's the tool's edge cutting into
                // material at a controlled depth, same as any other engagement cut, not a clearance
                // traverse. Modeling it as a rapid (the original shape of this cycle) meant a real
                // cutting action was invisible to material removal and would misread as a collision
                // now that stock-aware checks exist.
                if (!TryMoveTo(startX, startZ, rapid: true))
                    return;
                if (!TryMoveTo(passStartX, startZ, rapid: false))
                    return;
                if (!TryMoveTo(passEndX, targetZ, rapid: false))
                    return;
                if (!TryMoveTo(startX, targetZ, rapid: true))
                    return;

                Messages.Add($"  Pass {p + 1}/{depths.Count}: depth {depth:F3}mm");
            }

            if (!TryMoveTo(startX, startZ, rapid: true))
                return;

            Messages.Add("G76: Threading complete");
        }

        // Standard equal-cutting-area degression: cumulative depth after pass n is proportional to
        // sqrt(n), so each successive pass removes less material as the thread gets deeper. Once the
        // shrinking increment would fall below the programmed minimum depth of cut, remaining passes
        // step by that minimum instead. Finishing/spring passes repeat at the full thread height with
        // no further infeed.
        private static List<double> BuildThreadDepthSchedule(double height, double firstCutDepth, double minDepthOfCut, int finishPasses)
        {
            var depths = new List<double>();
            if (firstCutDepth < 1e-6)
                firstCutDepth = minDepthOfCut;

            double cumulative = 0;
            int n = 1;
            while (cumulative < height - 1e-9 && depths.Count < 200)
            {
                var nextCumulative = firstCutDepth * Math.Sqrt(n);
                var increment = nextCumulative - cumulative;
                if (increment < minDepthOfCut)
                {
                    increment = minDepthOfCut;
                    nextCumulative = cumulative + increment;
                }
                if (nextCumulative >= height)
                    nextCumulative = height;

                depths.Add(nextCumulative);
                cumulative = nextCumulative;
                n++;
            }

            for (int i = 0; i < finishPasses; i++)
                depths.Add(height);

            return depths;
        }

        private void ExecuteGroovingCycle(GCodeParser.Block block)
        {
            if (!CheckToolType(75, "grooving", ToolType.Grooving, ToolType.IdGrooving))
                return;

            var (targetX, targetZ, hasMotion) = ResolveTargetXZ(X, Z, block);
            if (!hasMotion)
            {
                Alarms.Add(new Alarm(79, "G75: Missing X/Z target"));
                return;
            }

            // P and Q are always in microns (1/1000 mm) on a real Fanuc control, regardless of
            // G20/G21 - a well-known quirk of these two address words in canned cycles.
            var peckX = block.Params.TryGetValue("P", out var pVal) ? Math.Abs(pVal) / 1000.0 : 0.5;
            var stepZ = block.Params.TryGetValue("Q", out var qVal) ? Math.Abs(qVal) / 1000.0 : 0.0;
            var retract = block.Params.TryGetValue("R", out var rVal) ? Math.Abs(ToMm(rVal)) : _groovingRetract;
            var feed = block.Params.TryGetValue("Feed", out var f) ? f : FeedRate;

            // External grooving plunges inward (X decreases toward targetX, deeper into the OD);
            // internal grooving plunges outward (X increases toward targetX, deeper into the bore
            // wall) - the two are mirror images of each other along X.
            var internalGroove = Offsets.GetOrCreateTool(_activeOffsetNumber).Type == ToolType.IdGrooving;

            var startX = X;
            var zPositions = BuildGroovePlunges(Z, targetZ, stepZ);
            Messages.Add($"G75: Grooving cycle, {zPositions.Count} plunge position(s), peck {peckX:F3}mm");

            FeedRate = feed;
            foreach (var z in zPositions)
            {
                if (!TryMoveTo(startX, z, rapid: true))
                    return;

                var depth = startX;
                if (!internalGroove)
                {
                    while (depth > targetX + 1e-9)
                    {
                        depth = Math.Max(targetX, depth - peckX);
                        if (!TryMoveTo(depth, z, rapid: false))
                            return;

                        if (depth > targetX + 1e-9 && !TryMoveTo(Math.Min(startX, depth + retract), z, rapid: true))
                            return;
                    }
                }
                else
                {
                    while (depth < targetX - 1e-9)
                    {
                        depth = Math.Min(targetX, depth + peckX);
                        if (!TryMoveTo(depth, z, rapid: false))
                            return;

                        if (depth < targetX - 1e-9 && !TryMoveTo(Math.Max(startX, depth - retract), z, rapid: true))
                            return;
                    }
                }

                if (!TryMoveTo(startX, z, rapid: true)) // clear the groove before the next plunge position
                    return;
            }

            Messages.Add($"G75: Grooving complete, retracted to X{startX:F2}");
        }

        // Wide grooves (Q > 0) get pecked at a sequence of Z positions stepping toward targetZ;
        // narrow single-width grooves just peck once at the current Z.
        private static List<double> BuildGroovePlunges(double startZ, double targetZ, double stepZ)
        {
            var positions = new List<double>();
            if (stepZ < 1e-9 || Math.Abs(targetZ - startZ) < 1e-9)
            {
                positions.Add(startZ);
                return positions;
            }

            var dir = Math.Sign(targetZ - startZ);
            var z = startZ;
            while (dir > 0 ? z < targetZ - 1e-6 : z > targetZ + 1e-6)
            {
                positions.Add(z);
                z += dir * stepZ;
            }
            positions.Add(targetZ);
            return positions;
        }

        // Walks the block range between two N-numbers, resolving each motion block's target (via a
        // scratch cursor, not the simulator's real X/Z) into an ordered polyline. Embedded G/M-codes
        // within the range are ignored - by convention the P/Q range is just the shape description.
        private List<(double X, double Z)> ExtractContour(List<GCodeParser.Block> blocks, int ns, int nf)
        {
            var contour = new List<(double X, double Z)>();

            var startIdx = blocks.FindIndex(b => b.Params.TryGetValue("N", out var n) && (int)n == ns);
            var endIdx = blocks.FindIndex(b => b.Params.TryGetValue("N", out var n) && (int)n == nf);

            if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
            {
                Alarms.Add(new Alarm(79, $"G70/71/72: Sequence numbers N{ns}-N{nf} not found"));
                return contour;
            }

            double cursorX = X, cursorZ = Z;
            for (int idx = startIdx; idx <= endIdx; idx++)
            {
                var block = blocks[idx];
                var (tx, tz, hasMotion) = ResolveTargetXZ(cursorX, cursorZ, block);
                if (!hasMotion)
                    continue;

                // Arcs embedded in the profile get walked as the actual curve (reusing the same
                // tessellation as real G02/G03 motion), not degraded to a straight start-to-end chord.
                var isArc = block.Commands.Any(c => c.Type == 'G' && (c.Code == 2 || c.Code == 3));
                if (isArc)
                {
                    var clockwise = block.Commands.Any(c => c.Type == 'G' && c.Code == 2);
                    double centerX = cursorX, centerZ = cursorZ;

                    if (block.Params.ContainsKey("I") || block.Params.ContainsKey("K"))
                    {
                        centerX = cursorX + (block.Params.TryGetValue("I", out var iv) ? ToMm(iv) : 0);
                        centerZ = cursorZ + (block.Params.TryGetValue("K", out var kv) ? ToMm(kv) : 0);
                    }
                    else if (block.Params.TryGetValue("R", out var rVal))
                    {
                        var center = ComputeArcCenterFromRadius(cursorX, cursorZ, tx, tz, ToMm(rVal), clockwise);
                        if (center.HasValue)
                            (centerX, centerZ) = center.Value;
                    }

                    contour.AddRange(ComputeArcPoints(cursorX, cursorZ, tx, tz, centerX, centerZ, clockwise));
                }
                else
                {
                    contour.Add((tx, tz));
                }

                cursorX = tx;
                cursorZ = tz;
            }

            return contour;
        }

        private void ExecuteFinishingCycle(GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            if (!block.Params.TryGetValue("P", out var ns) || !block.Params.TryGetValue("Q", out var nf))
            {
                Alarms.Add(new Alarm(79, "G70: Missing P/Q sequence numbers"));
                return;
            }

            var contour = ExtractContour(blocks, (int)ns, (int)nf);
            if (contour.Count == 0)
                return;

            if (block.Params.TryGetValue("Feed", out var feed))
                FeedRate = feed;

            Messages.Add($"G70: Finishing pass, {contour.Count} points");

            foreach (var (tx, tz) in contour)
            {
                if (!TryMoveTo(tx, tz, rapid: false))
                    return;
            }

            Messages.Add($"G70: Finish complete X{X:F2} Z{Z:F2}");
        }

        private void ExecuteRoughingCycle(bool isFacing, GCodeParser.Block block, List<GCodeParser.Block> blocks)
        {
            var cycleName = isFacing ? "facing" : "turning";
            if (!CheckToolType(isFacing ? 72 : 71, cycleName, ToolType.OdTurning, ToolType.IdBoring))
                return;

            if (!block.Params.TryGetValue("P", out var ns) || !block.Params.TryGetValue("Q", out var nf))
            {
                Alarms.Add(new Alarm(79, "G71/G72: Missing P/Q sequence numbers"));
                return;
            }

            var rawContour = ExtractContour(blocks, (int)ns, (int)nf);
            if (rawContour.Count == 0)
                return;

            var du = block.Params.TryGetValue("U", out var duVal) ? ToMm(duVal) : 0;
            var dw = block.Params.TryGetValue("W", out var dwVal) ? ToMm(dwVal) : 0;
            var roughFeed = block.Params.TryGetValue("Feed", out var f) ? f : FeedRate;

            var contour = new List<(double X, double Z)> { (X, Z) };
            contour.AddRange(rawContour);

            // Simplification: shift every contour point uniformly by the finish allowance in both
            // axes, rather than a true per-segment perpendicular offset (see plan notes).
            var offsetContour = contour.Select(p => (X: p.X + du, Z: p.Z + dw)).ToList();

            var code = isFacing ? 72 : 71;
            Messages.Add($"G{code}: Roughing cycle, {offsetContour.Count} contour points, allowance U{du:F2} W{dw:F2}");

            RunRoughingPasses(offsetContour, roughFeed, isFacing, code);
        }

        // Generic pass-clamping: for a pass at depth `passPrimary` on the stepping axis, the cut
        // envelope at any point along the walking axis is max(passPrimary, contour's primary there) -
        // material can only be removed down to the finish contour, never past it. Walking this per
        // segment (including the crossing point where the raw contour first reaches passPrimary)
        // naturally reproduces stepped relief cuts at contour vertices and smooth cuts along tapers.
        private static List<(double Primary, double Secondary)> ClampPassPath(
            List<(double Primary, double Secondary)> contour, double passPrimary)
        {
            var path = new List<(double, double)>();
            void AddIfNew(double p, double s)
            {
                if (path.Count == 0 || Math.Abs(path[^1].Item1 - p) > 1e-9 || Math.Abs(path[^1].Item2 - s) > 1e-9)
                    path.Add((p, s));
            }

            for (int i = 0; i < contour.Count - 1; i++)
            {
                var a = contour[i];
                var b = contour[i + 1];
                var ap = Math.Max(passPrimary, a.Primary);
                var bp = Math.Max(passPrimary, b.Primary);

                AddIfNew(ap, a.Secondary);

                if ((a.Primary - passPrimary) * (b.Primary - passPrimary) < 0)
                {
                    var t = (passPrimary - a.Primary) / (b.Primary - a.Primary);
                    var crossSecondary = a.Secondary + t * (b.Secondary - a.Secondary);
                    AddIfNew(passPrimary, crossSecondary);
                }

                AddIfNew(bp, b.Secondary);
            }

            return path;
        }

        // Shared by G71 (primary axis = X, stepping toward the finish diameter) and G72 (primary
        // axis = Z, stepping toward the finish face) - both assume external turning (stock outside
        // the finish contour) and a monotonic contour on the walking axis.
        private void RunRoughingPasses(List<(double X, double Z)> offsetContour, double feed, bool isFacing, int code)
        {
            (double Primary, double Secondary) ToGeneric((double X, double Z) p) => isFacing ? (p.Z, p.X) : (p.X, p.Z);
            (double X, double Z) FromGeneric(double primary, double secondary) => isFacing ? (secondary, primary) : (primary, secondary);

            var genericContour = offsetContour.Select(ToGeneric).ToList();
            var startPrimary = genericContour[0].Primary;
            var minPrimary = genericContour.Min(p => p.Primary);

            if (startPrimary <= minPrimary + 1e-6)
            {
                Messages.Add($"G{code}: Nothing to remove - stock already at or inside the finish allowance");
                return;
            }

            var passCount = Math.Max(1, (int)Math.Ceiling((startPrimary - minPrimary) / _roughingDepth));
            var approachSecondary = genericContour[0].Secondary;

            // Always fully clear of the ORIGINAL stock (not just the previous pass's own depth) -
            // safe to reposition across the whole part length at this primary value regardless of
            // how many passes have run, since nothing has ever been cut wider than startPrimary.
            var clearPrimary = startPrimary + _roughingRetract;
            var currentSecondary = ToGeneric((X, Z)).Secondary;

            for (int pass = 1; pass <= passCount; pass++)
            {
                var passPrimary = Math.Max(minPrimary, startPrimary - pass * _roughingDepth);
                var path = ClampPassPath(genericContour, passPrimary);
                if (path.Count == 0)
                    continue;

                // Two single-axis rapids instead of one diagonal shortcut: retract to the clear
                // primary at the CURRENT secondary first (safe - the prior pass already cleared its
                // entire traversed span at that position), then reposition to the approach secondary
                // while still at the clear primary (safe everywhere, for the whole part length).
                var (retractX, retractZ) = FromGeneric(clearPrimary, currentSecondary);
                if (!TryMoveTo(retractX, retractZ, rapid: true))
                    return;

                var (approachX, approachZ) = FromGeneric(clearPrimary, approachSecondary);
                if (!TryMoveTo(approachX, approachZ, rapid: true))
                    return;

                FeedRate = feed;
                foreach (var (primary, secondary) in path)
                {
                    var (px, pz) = FromGeneric(primary, secondary);
                    if (!TryMoveTo(px, pz, rapid: false))
                        return;
                }

                currentSecondary = path[^1].Secondary;
                Messages.Add($"  Pass {pass}/{passCount}: depth {passPrimary:F2}mm");
            }

            // Return to the exact original diameter (matching pre-fix behavior, so G70 finishing
            // starts from the same place it always has), reached via the same safe two-leg pattern.
            var (finalClearX, finalClearZ) = FromGeneric(clearPrimary, currentSecondary);
            if (!TryMoveTo(finalClearX, finalClearZ, rapid: true))
                return;
            var (finalApproachX, finalApproachZ) = FromGeneric(clearPrimary, approachSecondary);
            if (!TryMoveTo(finalApproachX, finalApproachZ, rapid: true))
                return;
            var (finalX, finalZ) = FromGeneric(startPrimary, approachSecondary);
            TryMoveTo(finalX, finalZ, rapid: true);

            Messages.Add($"G{code}: Roughing complete, {passCount} passes");
        }
    }
}
