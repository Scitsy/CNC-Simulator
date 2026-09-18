using System;
using System.Collections.Generic;

namespace FanucSimulator
{
    // Answers "what did the machine look like at time t" for a recorded MotionTimeline: where the tool
    // was, how much stock was left, which block was running, how far the current move still had to
    // go, and how much of the log had been produced. No WPF - the UI drives it frame by frame, and
    // the engine tests drive it directly.
    //
    // Moving forward is incremental: only carves the playhead has newly passed are applied. Moving
    // backward rebuilds from the run's start snapshot. Carving is monotonic (a carve only ever
    // removes material), so carving part of the current segment each frame and more of it next frame
    // is safe - the fuller carve always contains the partial one.
    public sealed class PlaybackCursor
    {
        public MotionTimeline Timeline { get; }

        // The stock at the current time. A working copy - never the engine's own profile.
        public StockProfile Stock { get; private set; }

        public double Time { get; private set; }
        public bool IsAtEnd => Time >= Timeline.Duration;

        // Events [0, _applied) have been fully carved. Events[_applied], if it has started, is the one
        // in progress.
        private int _applied;

        // Markers [0, _markersStarted) have been reached.
        private int _markersStarted;

        public PlaybackCursor(MotionTimeline timeline)
        {
            Timeline = timeline;
            Stock = timeline.StartStock.Clone();
            Seek(0);
        }

        public void Seek(double t)
        {
            t = Math.Clamp(t, 0, Timeline.Duration);
            if (t < Time)
            {
                Stock = Timeline.StartStock.Clone();
                _applied = 0;
                _markersStarted = 0;
            }
            Time = t;

            var events = Timeline.Events;
            var atEnd = IsAtEnd;
            while (_applied < events.Count && (atEnd || events[_applied].EndTime <= t))
            {
                Carve(events[_applied], 1.0);
                _applied++;
            }

            var current = CurrentEvent;
            if (current != null && current.Duration > 0)
                Carve(current, (t - current.StartTime) / current.Duration);

            var markers = Timeline.Markers;
            while (_markersStarted < markers.Count && (atEnd || markers[_markersStarted].Time <= t))
                _markersStarted++;
        }

        // The event in progress at the current time, or null between events and at the end.
        public TimelineEvent? CurrentEvent =>
            !IsAtEnd && _applied < Timeline.Events.Count && Timeline.Events[_applied].StartTime <= Time
                ? Timeline.Events[_applied]
                : null;

        private double CurrentFraction
        {
            get
            {
                var ev = CurrentEvent;
                return ev == null || ev.Duration <= 0 ? 0 : Math.Clamp((Time - ev.StartTime) / ev.Duration, 0, 1);
            }
        }

        // Tool position in render space (what the canvas draws). Null when the run recorded no motion.
        public (double X, double Z)? ToolRender
        {
            get
            {
                var ev = CurrentEvent;
                if (ev != null)
                {
                    var f = CurrentFraction;
                    return (Lerp(ev.FromRenderX, ev.ToRenderX, f), Lerp(ev.FromRenderZ, ev.ToRenderZ, f));
                }
                if (_applied > 0)
                {
                    var last = Timeline.Events[_applied - 1];
                    return (last.ToRenderX, last.ToRenderZ);
                }
                if (Timeline.Events.Count > 0)
                    return (Timeline.Events[0].FromRenderX, Timeline.Events[0].FromRenderZ);
                return null;
            }
        }

        // Tool position in programmed (work) coordinates - what the POS counters show.
        public (double X, double Z)? ToolProgrammed
        {
            get
            {
                var ev = CurrentEvent;
                if (ev != null)
                {
                    var f = CurrentFraction;
                    return (Lerp(ev.FromX, ev.ToX, f), Lerp(ev.FromZ, ev.ToZ, f));
                }
                if (_applied > 0)
                {
                    var last = Timeline.Events[_applied - 1];
                    return (last.ToX, last.ToZ);
                }
                if (Timeline.Events.Count > 0)
                    return (Timeline.Events[0].FromX, Timeline.Events[0].FromZ);
                return null;
            }
        }

        // DISTANCE TO GO: what's left of the current move, per axis, in programmed coordinates.
        public (double X, double Z) DistanceToGo
        {
            get
            {
                var ev = CurrentEvent;
                if (ev == null)
                    return (0, 0);
                var f = CurrentFraction;
                return (ev.ToX - Lerp(ev.FromX, ev.ToX, f), ev.ToZ - Lerp(ev.FromZ, ev.ToZ, f));
            }
        }

        // The program line being executed: the moving block's line while a move is in progress,
        // otherwise the last block reached.
        public int CurrentLine
        {
            get
            {
                var ev = CurrentEvent;
                if (ev != null)
                    return ev.Line;
                for (int i = _markersStarted - 1; i >= 0; i--)
                    if (Timeline.Markers[i].Line != 0)
                        return Timeline.Markers[i].Line;
                return 0;
            }
        }

        // The machine state to show now: the moving block's while a move is in progress, otherwise
        // the state as of the last block reached.
        public MachineStateSnapshot? State
        {
            get
            {
                var ev = CurrentEvent;
                if (ev != null)
                    return ev.State;
                if (_markersStarted > 0)
                    return Timeline.Markers[_markersStarted - 1].State;
                return null;
            }
        }

        // How much of the run's log to show now. A block's lines appear as soon as the block starts,
        // which is when an operator would want to read "now doing G71...".
        public (int Messages, int Warnings, int Alarms) RevealedLog
        {
            get
            {
                var markers = Timeline.Markers;
                if (markers.Count == 0)
                    return (0, 0, 0);
                var m = markers[Math.Min(_markersStarted, markers.Count - 1)];
                return (m.MessageCount, m.WarningCount, m.AlarmCount);
            }
        }

        // Every segment drawn so far this run, the in-progress one cut off at the tool.
        public IEnumerable<(double X1, double Z1, double X2, double Z2, TimelineEventKind Kind)> SegmentsSoFar()
        {
            var events = Timeline.Events;
            for (int i = 0; i < _applied && i < events.Count; i++)
            {
                var ev = events[i];
                if (ev.Kind != TimelineEventKind.Dwell)
                    yield return (ev.FromRenderX, ev.FromRenderZ, ev.ToRenderX, ev.ToRenderZ, ev.Kind);
            }

            var current = CurrentEvent;
            if (current != null && current.Kind != TimelineEventKind.Dwell && ToolRender is { } tip)
                yield return (current.FromRenderX, current.FromRenderZ, tip.X, tip.Z, current.Kind);
        }

        // The start time of the first collision segment beginning after 'from' and no later than
        // 'to', if any. Playback stops there, with the tool poised at the start of the rapid that
        // would crash, rather than after the damage is drawn.
        public double? FirstCollisionBetween(double from, double to)
        {
            foreach (var ev in Timeline.Events)
            {
                if (ev.StartTime > to)
                    break;
                if (ev.Kind == TimelineEventKind.Collision && ev.StartTime > from)
                    return ev.StartTime;
            }
            return null;
        }

        private void Carve(TimelineEvent ev, double fraction)
        {
            if (ev.Carve == CarveKind.None)
                return;

            fraction = Math.Clamp(fraction, 0, 1);
            var z2 = Lerp(ev.CarveZ1, ev.CarveZ2, fraction);
            var x2 = Lerp(ev.CarveX1, ev.CarveX2, fraction);
            if (ev.Carve == CarveKind.Outer)
                Stock.CarveOuter(ev.CarveZ1, ev.CarveX1, z2, x2);
            else
                Stock.CarveInner(ev.CarveZ1, ev.CarveX1, z2, x2);
        }

        // Exact at f == 1 (returns b itself), so a fully carved segment matches the engine's own carve
        // to the last bit rather than to within rounding.
        private static double Lerp(double a, double b, double f) => f >= 1 ? b : a + (b - a) * f;
    }
}
