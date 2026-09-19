using System.Collections.Generic;

namespace FanucSimulator
{
    public enum TimelineEventKind { Rapid, Feed, Collision, Dwell }

    // Which boundary a segment carved, if any - recorded exactly as LatheSimulator.MoveTo passed it
    // to StockProfile, so replaying the carves reproduces the engine's stock bit for bit.
    public enum CarveKind { None, Outer, Inner }

    // The machine state the screen shows alongside motion. Captured whenever something is recorded,
    // so playback can show what was true at that moment rather than what is true at the end.
    // SpindleRpm is the spindle's actual speed then, which lags the commanded speed while it ramps.
    public readonly record struct MachineStateSnapshot(
        double SpindleRpm, int SpindleDir, bool CoolantOn, int Tool, double FeedRate, bool Inch, bool FeedPerRev,
        MotionMode Motion, CutterComp Comp, int WorkOffset, bool Css);

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
        public double FromRenderX { get; set; }
        public double FromRenderZ { get; set; }
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
        // Settable: a cut under nose-radius compensation is only carved once the next move has
        // fixed where its end corner is (see LatheSimulator.FlushPendingCarve).
        public CarveKind Carve { get; set; }
        public double CarveZ1 { get; set; }
        public double CarveX1 { get; set; }
        public double CarveZ2 { get; set; }
        public double CarveX2 { get; set; }

        // Above 0: the carve is a round tool nose of this radius swept along (CarveZ1, CarveX1) ->
        // (CarveZ2, CarveX2), which is then the path of the nose's centre. 0: a point carve.
        public double CarveNoseRadius { get; set; }

        // The finish (Ra, micrometres; NaN = not tracked) this carve leaves, and whether each end
        // reaches past into the next sample - false only at the joins of a cut split into pieces
        // because its finish changes along it (the spindle still getting up to speed).
        public double CarveRa { get; set; } = double.NaN;
        public bool CarveReachStart { get; set; } = true;
        public bool CarveReachEnd { get; set; } = true;

        public int Line { get; init; }
        public MachineStateSnapshot State { get; init; }

        // Whether the FEED override dial scales this move: cutting feeds do; rapids have their own
        // dial; threading ignores the feed override (the lead is tied to the spindle), as on FANUC.
        public bool FeedOverrideApplies { get; init; }

        // How much of the log existed just before this move began. Canned cycles log pass by pass
        // inside one block, so revealing the log by block alone ran a whole G71 ahead of the tool;
        // tying it to moves keeps it at most one move ahead.
        public int MessagesBefore { get; init; }
        public int WarningsBefore { get; init; }
        public int AlarmsBefore { get; init; }
    }

    // Recorded before each block runs. Carries the log counts at that instant so playback can reveal
    // console lines, warnings and alarms as the playhead reaches them rather than all at once.
    public sealed class BlockMarker
    {
        public int Seq { get; init; }
        public double Time { get; init; }
        public int Line { get; init; } // 0 for the closing marker recorded when the run stops

        // The block's N word, or -1 if it has none. Blocks that carry only an N take no time, so the
        // UI would almost never catch them as "the running line" - recording it here lets playback
        // show the last N-number reached, the way the control does.
        public int SequenceNumber { get; init; } = -1;
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

        // The override dials as they stood when the run was recorded. Playback compares the dials'
        // live positions against these to speed up or slow down what is still to come.
        public double RecordedFeedOverride { get; init; } = 1.0;
        public double RecordedRapidMmPerMin { get; init; } = MachineSpec.RapidTraverseMmPerMin;

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
