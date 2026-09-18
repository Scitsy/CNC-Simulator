using System;

namespace FanucSimulator
{
    // A 2D radius-as-function-of-Z cross-section of the remaining stock - the standard lightweight
    // lathe-sim approach, not a full solid model. Two piecewise-linear boundaries, discretized into
    // a fixed sample grid across the stock's Z span:
    //   OuterX[i] - current outer diameter of remaining material (starts flat at StockDiameter).
    //   InnerX[i] - current bore diameter, 0 = solid (starts flat at 0).
    // A cut clamps one boundary toward the other; the visible "material" at any Z is the ring
    // between InnerX and OuterX (or a solid disk where InnerX is still 0).
    public class StockProfile
    {
        public const int Resolution = 400;

        public double ZStart { get; }
        public double ZEnd { get; }
        public double[] OuterX { get; }
        public double[] InnerX { get; }

        // Surface roughness (Ra, micrometres) the last cut to reach each sample's surface left
        // behind. NaN = not tracked: uncut stock, or a cut whose finish isn't modelled (drilling,
        // grooving, threading).
        public double[] OuterRa { get; }
        public double[] InnerRa { get; }

        public StockProfile(double stockLength, double stockDiameter)
        {
            ZStart = -stockLength;
            ZEnd = 0;
            OuterX = new double[Resolution + 1];
            InnerX = new double[Resolution + 1];
            OuterRa = new double[Resolution + 1];
            InnerRa = new double[Resolution + 1];
            for (int i = 0; i <= Resolution; i++)
            {
                OuterX[i] = stockDiameter;
                InnerX[i] = 0;
                OuterRa[i] = double.NaN;
                InnerRa[i] = double.NaN;
            }
        }

        public double SampleZ(int i) => ZStart + (ZEnd - ZStart) * i / Resolution;

        // A detached copy - used to snapshot the stock at the start of a run, so playback can replay
        // the run's carves onto it without touching the engine's live profile.
        public StockProfile Clone()
        {
            var copy = new StockProfile(ZEnd - ZStart, 0);
            Array.Copy(OuterX, copy.OuterX, OuterX.Length);
            Array.Copy(InnerX, copy.InnerX, InnerX.Length);
            Array.Copy(OuterRa, copy.OuterRa, OuterRa.Length);
            Array.Copy(InnerRa, copy.InnerRa, InnerRa.Length);
            return copy;
        }

        // Material removed from the outside in (OD turning, facing, grooving, threading) - clamps
        // the outer boundary down to whatever the tool's edge swept through.
        // `ra` is the finish this cut leaves wherever it ends up being the surface. `reachStart` /
        // `reachEnd`: see Carve - false only where one cut has been split into pieces.
        public void CarveOuter(double z1, double x1, double z2, double x2, double ra = double.NaN,
                               bool reachStart = true, bool reachEnd = true) =>
            Carve(z1, x1, z2, x2, OuterX, OuterRa, ra, min: true, reachStart, reachEnd);

        // Material removed from the inside out (boring, drilling) - clamps the inner boundary up to
        // whatever diameter the tool opened.
        public void CarveInner(double z1, double x1, double z2, double x2, double ra = double.NaN,
                               bool reachStart = true, bool reachEnd = true) =>
            Carve(z1, x1, z2, x2, InnerX, InnerRa, ra, min: false, reachStart, reachEnd);

        // Read-only check for rapid-move safety: does the straight segment from (z1,x1) to (z2,x2)
        // pass through remaining material anywhere along its length? A small tolerance keeps this
        // from firing on a legitimate near-flush approach or the sub-mm re-engagement a threading
        // pass makes into its own previous groove - only a genuine plow into solid, untouched
        // material (well beyond the tolerance band) counts as a collision.
        public bool IntersectsMaterial(double z1, double x1, double z2, double x2, double tolerance = 0.3)
        {
            var zLo = Math.Max(Math.Min(z1, z2), ZStart);
            var zHi = Math.Min(Math.Max(z1, z2), ZEnd);
            if (zHi < zLo)
                return false;

            var iLo = Math.Clamp((int)Math.Floor((zLo - ZStart) / (ZEnd - ZStart) * Resolution), 0, Resolution);
            var iHi = Math.Clamp((int)Math.Ceiling((zHi - ZStart) / (ZEnd - ZStart) * Resolution), 0, Resolution);
            var sameZ = Math.Abs(z2 - z1) < 1e-9;

            for (int i = iLo; i <= iHi; i++)
            {
                var innerBound = InnerX[i] + tolerance;
                var outerBound = OuterX[i] - tolerance;
                if (outerBound <= innerBound)
                    continue; // no material band left at this sample once tolerance is applied

                if (sameZ)
                {
                    var xMin = Math.Min(x1, x2);
                    var xMax = Math.Max(x1, x2);
                    if (xMax > innerBound && xMin < outerBound)
                        return true;
                }
                else
                {
                    var t = Math.Clamp((SampleZ(i) - z1) / (z2 - z1), 0, 1);
                    var x = x1 + t * (x2 - x1);
                    if (x > innerBound && x < outerBound)
                        return true;
                }
            }
            return false;
        }

        // A cut reaches out to the sample just past each of its ends (carving it to that end's X),
        // standing in for the width of the tool's nose - without it, a rapid pulling straight out
        // from the end of a cut would clip the very sample the tool just cleared. Where one cut is
        // recorded as several pieces (the finish changing along it), the joins between pieces must
        // not reach past - on a rising taper that would notch the surface at every join - so the
        // caller turns reachStart/reachEnd off there and only the cut's real ends reach.
        private void Carve(double z1, double x1, double z2, double x2, double[] profile, double[] finish, double ra, bool min,
                           bool reachStart = true, bool reachEnd = true)
        {
            var zLo = Math.Max(Math.Min(z1, z2), ZStart);
            var zHi = Math.Min(Math.Max(z1, z2), ZEnd);
            if (zHi < zLo)
                return; // segment entirely outside the stock (e.g. a clearance move past the face)

            var sameZ = Math.Abs(z2 - z1) < 1e-9;
            var reachLo = sameZ || (z1 <= z2 ? reachStart : reachEnd);
            var reachHi = sameZ || (z1 <= z2 ? reachEnd : reachStart);
            var positionLo = (zLo - ZStart) / (ZEnd - ZStart) * Resolution;
            var positionHi = (zHi - ZStart) / (ZEnd - ZStart) * Resolution;
            var iLo = Math.Clamp(reachLo ? (int)Math.Floor(positionLo) : (int)Math.Ceiling(positionLo - 1e-9), 0, Resolution);
            var iHi = Math.Clamp(reachHi ? (int)Math.Ceiling(positionHi) : (int)Math.Floor(positionHi + 1e-9), 0, Resolution);

            for (int i = iLo; i <= iHi; i++)
            {
                double x;
                if (sameZ)
                {
                    // Plunge/facing cut at a fixed Z - the tool swept the full range between the two
                    // endpoints, so the deepest (outer) / widest (inner) point reached is what counts,
                    // not an interpolated value (there's nothing to interpolate along).
                    x = min ? Math.Min(x1, x2) : Math.Max(x1, x2);
                }
                else
                {
                    var t = Math.Clamp((SampleZ(i) - z1) / (z2 - z1), 0, 1);
                    x = x1 + t * (x2 - x1);
                }
                profile[i] = min ? Math.Min(profile[i], x) : Math.Max(profile[i], x);

                // This cut is the surface here now (or re-cut it exactly, like a spring pass), so the
                // finish is whatever it left.
                if (min ? x <= profile[i] + 1e-9 : x >= profile[i] - 1e-9)
                    finish[i] = ra;

                // Defensive: a broken program (e.g. boring past the OD) shouldn't be able to invert
                // the two boundaries and produce a self-intersecting render.
                if (OuterX[i] < InnerX[i])
                {
                    if (min) OuterX[i] = InnerX[i];
                    else InnerX[i] = OuterX[i];
                }
            }
        }
    }
}
