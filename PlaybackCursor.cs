using System;
using System.Collections.Generic;
using System.Linq;

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

        // Every event and marker in the order the engine recorded them, with the log counts at that
        // point. The log shown is the counts at the first record not yet reached - so lines written
        // between two moves appear as the first of those moves plays, never further ahead.
        private readonly (double Time, int Messages, int Warnings, int Alarms)[] _records;
        private readonly (int Messages, int Warnings, int Alarms) _finalLog;
        private int _recordsStarted;

        public PlaybackCursor(MotionTimeline timeline)
        {
            Timeline = timeline;
            Stock = timeline.StartStock.Clone();

            var merged = new List<(int Seq, double Time, int M, int W, int A)>();
            foreach (var ev in timeline.Events)
                merged.Add((ev.Seq, ev.StartTime, ev.MessagesBefore, ev.WarningsBefore, ev.AlarmsBefore));
            foreach (var mk in timeline.Markers)
                merged.Add((mk.Seq, mk.Time, mk.MessageCount, mk.WarningCount, mk.AlarmCount));
            merged.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            _records = merged.Select(r => (r.Time, r.M, r.W, r.A)).ToArray();

            // The closing marker is the last thing recorded, so it carries the run's full log.
            _finalLog = timeline.Markers.Count > 0
                ? (timeline.Markers[^1].MessageCount, timeline.Markers[^1].WarningCount, timeline.Markers[^1].AlarmCount)
                : (0, 0, 0);

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
                _recordsStarted = 0;
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

            while (_recordsStarted < _records.Length && (atEnd || _records[_recordsStarted].Time <= t))
                _recordsStarted++;
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

        // The last N-number reached this run, or -1 if none yet.
        public int SequenceNumber
        {
            get
            {
                for (int i = _markersStarted - 1; i >= 0; i--)
                    if (Timeline.Markers[i].SequenceNumber >= 0)
                        return Timeline.Markers[i].SequenceNumber;
                return -1;
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

        // How much of the run's log to show now: everything the engine had written by the first
        // event or marker playback hasn't reached yet. A move's own lines therefore appear while it
        // plays, and a canned cycle's "pass N" lines appear pass by pass rather than all at once.
        public (int Messages, int Warnings, int Alarms) RevealedLog =>
            _recordsStarted < _records.Length
                ? (_records[_recordsStarted].Messages, _records[_recordsStarted].Warnings, _records[_recordsStarted].Alarms)
                : _finalLog;

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
            // Part-way through, the moving end is where the tool is, not a real end of the cut.
            var reachEnd = fraction >= 1 && ev.CarveReachEnd;
            if (ev.Carve == CarveKind.Outer)
                Stock.CarveOuter(ev.CarveZ1, ev.CarveX1, z2, x2, ev.CarveRa, ev.CarveReachStart, reachEnd);
            else
                Stock.CarveInner(ev.CarveZ1, ev.CarveX1, z2, x2, ev.CarveRa, ev.CarveReachStart, reachEnd);
        }

        // Exact at f == 1 (returns b itself), so a fully carved segment matches the engine's own carve
        // to the last bit rather than to within rounding.
        private static double Lerp(double a, double b, double f) => f >= 1 ? b : a + (b - a) * f;
    }
}
