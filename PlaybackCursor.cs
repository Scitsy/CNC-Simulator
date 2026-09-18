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

        // How far into Events[_partialIndex] has been carved (it is the event in progress). Carving
        // continues from there rather than redoing the whole segment each frame, so a change to
        // FinishScale mid-cut only affects the part cut after it - like turning the dial mid-pass.
        private int _partialIndex = -1;
        private double _partialFraction;

        // Multiplies the recorded finish (Ra) of whatever is carved from now on, for moves the FEED
        // override applies to. 1 = as recorded. The UI sets it from the live FEED dial: Ra goes with
        // feed per rev squared, so (live / recorded override)^2.
        public double FinishScale { get; set; } = 1.0;

        // Mirrors LatheSimulator's cap, so a scaled finish never reads rougher than the engine would say.
        private const double MaxTrackedRa = 50.0;

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
                _partialIndex = -1;
                _markersStarted = 0;
                _recordsStarted = 0;
            }
            Time = t;

            var events = Timeline.Events;
            var atEnd = IsAtEnd;
            while (_applied < events.Count && (atEnd || events[_applied].EndTime <= t))
            {
                CarveTo(_applied, 1.0);
                _applied++;
            }

            var current = CurrentEvent;
            if (current != null && current.Duration > 0)
                CarveTo(_applied, (t - current.StartTime) / current.Duration);

            var markers = Timeline.Markers;
            while (_markersStarted < markers.Count && (atEnd || markers[_markersStarted].Time <= t))
                _markersStarted++;

            while (_recordsStarted < _records.Length && (atEnd || _records[_recordsStarted].Time <= t))
                _recordsStarted++;
        }

        // ---- Playing at live override rates ----
        // `rate` says how fast each event plays against how it was recorded: timeline seconds per
        // second of machine time. 1 = as recorded, 2 = twice as fast (the override turned up), 0 =
        // stopped (FEED at 0%: the machine is still in cycle, but the axes don't move).

        // Where the playhead gets to after `machineSeconds` of machine time from now, and whether it
        // stopped on a rate-0 event (in which case all of that time was spent standing still).
        public (double Time, bool Stalled) TimeAfter(double machineSeconds, Func<TimelineEvent, double> rate)
        {
            var t = Time;
            var budget = machineSeconds;
            var events = Timeline.Events;
            var i = Math.Min(_applied, events.Count);
            while (budget > 0 && t < Timeline.Duration)
            {
                while (i < events.Count && events[i].EndTime <= t)
                    i++;
                if (i >= events.Count)
                    return (Timeline.Duration, false);

                var ev = events[i];
                var r = rate(ev);
                if (r <= 0)
                    return (t, true);
                var needed = (ev.EndTime - t) / r;
                if (needed <= budget)
                {
                    budget -= needed;
                    t = ev.EndTime;
                    i++;
                }
                else
                {
                    t += budget * r;
                    budget = 0;
                }
            }
            return (Math.Min(t, Timeline.Duration), false);
        }

        // Machine time to play the timeline from `from` to `to` at the given rates. A rate-0 event
        // counts at its recorded time: this is for totting up a run that did get through it.
        public double MachineSecondsBetween(double from, double to, Func<TimelineEvent, double> rate)
        {
            var total = 0.0;
            foreach (var ev in Timeline.Events)
            {
                var start = Math.Max(ev.StartTime, from);
                var end = Math.Min(ev.EndTime, to);
                if (end <= start)
                    continue;
                var r = rate(ev);
                total += (end - start) / (r > 0 ? r : 1);
            }
            return total;
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

        // Carves event `index` from wherever it got to up to `fraction`. An event carved in one go
        // (the usual case, and always when seeking straight to the end) is one carve with exactly the
        // engine's arguments, so the replay matches the engine bit for bit.
        private void CarveTo(int index, double fraction)
        {
            var ev = Timeline.Events[index];
            var from = _partialIndex == index ? _partialFraction : 0.0;
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction >= 1)
                _partialIndex = -1;
            else
                (_partialIndex, _partialFraction) = (index, Math.Max(from, fraction));

            if (ev.Carve == CarveKind.None || fraction <= from)
                return;

            var (z1, x1) = from <= 0 ? (ev.CarveZ1, ev.CarveX1) : (Lerp(ev.CarveZ1, ev.CarveZ2, from), Lerp(ev.CarveX1, ev.CarveX2, from));
            var z2 = Lerp(ev.CarveZ1, ev.CarveZ2, fraction);
            var x2 = Lerp(ev.CarveX1, ev.CarveX2, fraction);
            // Only the cut's real ends reach into the next sample; where carving stopped part-way (the
            // tool's position) or picks up again is a join, not an end.
            var reachStart = from <= 0 && ev.CarveReachStart;
            var reachEnd = fraction >= 1 && ev.CarveReachEnd;
            var ra = ev.CarveRa;
            if (ev.FeedOverrideApplies && FinishScale != 1.0 && !double.IsNaN(ra))
                ra = Math.Min(ra * FinishScale, MaxTrackedRa);
            if (ev.Carve == CarveKind.Outer)
                Stock.CarveOuter(z1, x1, z2, x2, ra, reachStart, reachEnd);
            else
                Stock.CarveInner(z1, x1, z2, x2, ra, reachStart, reachEnd);
        }

        // Exact at f == 1 (returns b itself), so a fully carved segment matches the engine's own carve
        // to the last bit rather than to within rounding.
        private static double Lerp(double a, double b, double f) => f >= 1 ? b : a + (b - a) * f;
    }
}
