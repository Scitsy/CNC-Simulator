using System.Collections.Generic;

namespace FanucSimulator
{
    public enum TimelineEventKind { Rapid, Feed, Collision, Dwell }

    // Which boundary a segment carved, if any - recorded exactly as LatheSimulator.MoveTo passed it
    // to StockProfile, so replaying the carves reproduces the engine's stock bit for bit.
    public enum CarveKind { None, Outer, Inner }

    // The machine state the screen shows alongside motion. Captured whenever something is recorded,
    // so playback can show what was true at that moment rather than what is true at the end.
    public readonly record struct MachineStateSnapshot(
        double SpindleRpm, int SpindleDir, bool CoolantOn, int Tool, double FeedRate, bool Inch, bool FeedPerRev);

    public sealed class TimelineEvent
    {
        public int Seq { get; init; }
        public double StartTime { get; set; }
        public double Duration { get; set; }
        public double EndTime => StartTime + Duration;
        public TimelineEventKind Kind { get; set; }

        // Render space - what the canvas draws: programmed position plus tool, wear and work offsets
        // and cutter comp. The "to" end can be moved after the fact by cutter-comp mitering, exactly
        // as LatheSimulator.ToolPath's own point is.
        public double FromRenderX { get; init; }
        public double FromRenderZ { get; init; }
        public double ToRenderX { get; set; }
        public double ToRenderZ { get; set; }

        // Programmed (work) coordinates - what the POS counters show.
        public double FromX { get; init; }
        public double FromZ { get; init; }
        public double ToX { get; init; }
        public double ToZ { get; init; }

        // The carve actually performed, in StockProfile's own (z1, x1, z2, x2) argument order. These
        // deliberately are NOT the render endpoints: mitering rewrites the display point after the
        // carve has already happened with the un-mitered one.
        public CarveKind Carve { get; init; }
        public double CarveZ1 { get; init; }
        public double CarveX1 { get; init; }
        public double CarveZ2 { get; init; }
        public double CarveX2 { get; init; }

        public int Line { get; init; }
        public MachineStateSnapshot State { get; init; }
    }

    // Recorded before each block runs. Carries the log counts at that instant so playback can reveal
    // console lines, warnings and alarms as the playhead reaches them rather than all at once.
    public sealed class BlockMarker
    {
        public int Seq { get; init; }
        public double Time { get; init; }
        public int Line { get; init; } // 0 for the closing marker recorded when the run stops
        public int MessageCount { get; init; }
        public int WarningCount { get; init; }
        public int AlarmCount { get; init; }
        public MachineStateSnapshot State { get; init; }
    }

    // Everything one RunProgram call did, in order, with real durations. The engine still runs
    // instantly; this is what lets the UI play the run back afterwards at machine speed.
    public sealed class MotionTimeline
    {
        public List<TimelineEvent> Events { get; } = new();
        public List<BlockMarker> Markers { get; } = new();

        // The stock as it stood when the run started. Replaying Events' carves onto a copy of this, in
        // order, reproduces the engine's stock at any point in the run.
        public StockProfile StartStock { get; }

        // How much of LatheSimulator.ToolPath already existed before this run - earlier runs' motion,
        // drawn as a fixed backdrop during playback.
        public int ToolPathStartCount { get; }

        public double Duration { get; internal set; }

        private int _nextSeq;

        public MotionTimeline(StockProfile startStock, int toolPathStartCount)
        {
            StartStock = startStock;
            ToolPathStartCount = toolPathStartCount;
        }

        internal int NextSeq() => _nextSeq++;

        // Gives the events from firstIndex onwards a new combined duration, in proportion to the times
        // they were recorded with, and re-times everything from there to the end. Used for arcs: each
        // chord is recorded with its straight-line time, then the set is rescaled to the arc's true
        // time (radius x sweep).
        internal void RescaleSince(int firstIndex, double totalSeconds)
        {
            if (firstIndex >= Events.Count)
                return;

            var provisional = 0.0;
            for (int i = firstIndex; i < Events.Count; i++)
                provisional += Events[i].Duration;

            var count = Events.Count - firstIndex;
            var t = Events[firstIndex].StartTime;
            for (int i = firstIndex; i < Events.Count; i++)
            {
                var ev = Events[i];
                ev.StartTime = t;
                ev.Duration = provisional > 0 ? ev.Duration / provisional * totalSeconds : totalSeconds / count;
                t += ev.Duration;
            }
            Duration = t;
        }
    }
}
