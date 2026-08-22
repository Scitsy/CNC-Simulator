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

        public StockProfile(double stockLength, double stockDiameter)
        {
            ZStart = -stockLength;
            ZEnd = 0;
            OuterX = new double[Resolution + 1];
            InnerX = new double[Resolution + 1];
            for (int i = 0; i <= Resolution; i++)
            {
                OuterX[i] = stockDiameter;
                InnerX[i] = 0;
            }
        }

        public double SampleZ(int i) => ZStart + (ZEnd - ZStart) * i / Resolution;

        // Material removed from the outside in (OD turning, facing, grooving, threading) - clamps
        // the outer boundary down to whatever the tool's edge swept through.
        public void CarveOuter(double z1, double x1, double z2, double x2) => Carve(z1, x1, z2, x2, OuterX, min: true);

        // Material removed from the inside out (boring, drilling) - clamps the inner boundary up to
        // whatever diameter the tool opened.
        public void CarveInner(double z1, double x1, double z2, double x2) => Carve(z1, x1, z2, x2, InnerX, min: false);

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

        private void Carve(double z1, double x1, double z2, double x2, double[] profile, bool min)
        {
            var zLo = Math.Max(Math.Min(z1, z2), ZStart);
            var zHi = Math.Min(Math.Max(z1, z2), ZEnd);
            if (zHi < zLo)
                return; // segment entirely outside the stock (e.g. a clearance move past the face)

            var iLo = Math.Clamp((int)Math.Floor((zLo - ZStart) / (ZEnd - ZStart) * Resolution), 0, Resolution);
            var iHi = Math.Clamp((int)Math.Ceiling((zHi - ZStart) / (ZEnd - ZStart) * Resolution), 0, Resolution);
            var sameZ = Math.Abs(z2 - z1) < 1e-9;

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
