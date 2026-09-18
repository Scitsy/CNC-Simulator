using System;
using System.Collections.Generic;

namespace FanucSimulator
{
    // The spindle's real speed as it ramps toward whatever is commanded. A spindle cannot jump to a
    // new speed: after M03/M04 or an S change it accelerates (or slows) at a steady rate, and a cut
    // that starts before it gets there is taken at the wrong speed.
    //
    // Speeds are signed (+ forward/M03, - reverse/M04), so a reversal ramps down through zero and
    // back up the other way. The ramp is linear: a fixed rpm per second, both up and down.
    public sealed class SpindleModel
    {
        public double ActualRpm { get; set; }

        // 0 or less = no ramp: the spindle reaches any commanded speed instantly.
        public double RampRpmPerSecond { get; set; }

        public SpindleModel(double rampRpmPerSecond) => RampRpmPerSecond = rampRpmPerSecond;

        public SpindleModel Clone() => new(RampRpmPerSecond) { ActualRpm = ActualRpm };

        private bool Instant => RampRpmPerSecond <= 0;

        public double SecondsToReach(double targetRpm) =>
            Instant ? 0 : Math.Abs(targetRpm - ActualRpm) / RampRpmPerSecond;

        public double RpmAfter(double seconds, double targetRpm)
        {
            if (Instant || seconds >= SecondsToReach(targetRpm))
                return targetRpm;
            return ActualRpm + Math.Sign(targetRpm - ActualRpm) * RampRpmPerSecond * seconds;
        }

        public void Advance(double seconds, double targetRpm) => ActualRpm = RpmAfter(Math.Max(0, seconds), targetRpm);

        // Revolutions turned over the next `seconds`, counted positive whichever way it turns.
        public double RevolutionsOver(double seconds, double targetRpm)
        {
            double revs = 0;
            foreach (var (start, length, fromRpm, toRpm) in Pieces(targetRpm))
            {
                if (seconds <= start)
                    break;
                var span = length is double l ? Math.Min(l, seconds - start) : seconds - start;
                var endRpm = length is double full && full > 0 ? fromRpm + (toRpm - fromRpm) * span / full : toRpm;
                revs += (Math.Abs(fromRpm) + Math.Abs(endRpm)) / 2 * span / 60;
            }
            return revs;
        }

        // Seconds to turn `revolutions` from now; +infinity if the spindle never turns that far
        // (stopped and staying stopped).
        public double SecondsForRevolutions(double revolutions, double targetRpm)
        {
            if (revolutions <= 0)
                return 0;

            double remaining = revolutions;
            foreach (var (start, length, fromRpm, toRpm) in Pieces(targetRpm))
            {
                var u0 = Math.Abs(fromRpm);
                if (length is not double span)
                    return u0 > 0 ? start + remaining * 60 / u0 : double.PositiveInfinity;

                var u1 = Math.Abs(toRpm);
                var pieceRevs = (u0 + u1) / 2 * span / 60;
                if (pieceRevs >= remaining)
                {
                    // Speed is linear across the piece: revs(t) = (u0 t + (u1 - u0) t^2 / (2 span)) / 60.
                    // Solved in the form that stays stable when the speed barely changes.
                    var a = (u1 - u0) / (2 * span);
                    var disc = u0 * u0 + 4 * a * 60 * remaining;
                    return start + 120 * remaining / (u0 + Math.Sqrt(Math.Max(0, disc)));
                }
                remaining -= pieceRevs;
            }
            return double.PositiveInfinity;
        }

        // The speed-over-time profile from now: linear pieces, split where the speed passes through
        // zero (so |rpm| is linear inside each), ending in an open-ended piece at the target.
        private IEnumerable<(double Start, double? Length, double FromRpm, double ToRpm)> Pieces(double targetRpm)
        {
            var t = 0.0;
            var rpm = ActualRpm;
            if (!Instant && Math.Abs(targetRpm - rpm) > 1e-9)
            {
                var crossesZero = rpm != 0 && Math.Sign(rpm) != Math.Sign(targetRpm);
                if (crossesZero)
                {
                    var toZero = Math.Abs(rpm) / RampRpmPerSecond;
                    yield return (t, toZero, rpm, 0);
                    t += toZero;
                    rpm = 0;
                }
                var toTarget = Math.Abs(targetRpm - rpm) / RampRpmPerSecond;
                if (toTarget > 0)
                {
                    yield return (t, toTarget, rpm, targetRpm);
                    t += toTarget;
                }
            }
            yield return (t, null, targetRpm, targetRpm);
        }
    }
}
