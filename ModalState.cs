namespace FanucSimulator
{
    public enum MotionMode { Rapid, Linear, ArcCw, ArcCcw }
    public enum UnitsMode { Metric, Inch }
    public enum FeedMode { PerMinute, PerRevolution }
    public enum SpindleMode { ConstantRpm, ConstantSurfaceSpeed }
    public enum CutterComp { Off, Left, Right }

    // The three single ("one-shot modal") canned cycles of FANUC G-code system A, plus G80 cancel.
    // Distinct from the G70-G76 multiple repetitive cycles, which execute once per trigger block and
    // are not modal at all.
    public enum CannedCycle { None, Turning, Threading, Facing }

    // Modal state persists across blocks until explicitly changed by another code in the same group.
    //
    // G-code system A (the FANUC lathe default, and what the reference machine's screen shows - its
    // modal block carries no G90/G91 pair at all): absolute vs incremental is not modal here. It is
    // expressed per-address - X/Z are always absolute, U/W always incremental - which is why there is
    // no PositionMode. G90/G92/G94 in system A are the single canned cycles above, not positioning
    // modes; systems B and C are the ones where G90/G91 mean absolute/incremental.
    public class ModalState
    {
        public MotionMode Motion { get; set; } = MotionMode.Rapid;
        public UnitsMode Units { get; set; } = UnitsMode.Metric;
        public FeedMode Feed { get; set; } = FeedMode.PerRevolution;
        public SpindleMode Spindle { get; set; } = SpindleMode.ConstantRpm;
        public CutterComp Comp { get; set; } = CutterComp.Off;
        public CannedCycle Cycle { get; set; } = CannedCycle.None;
        public int ActiveWorkOffset { get; set; } = 54;
        public double? MaxCssRpm { get; set; }
        public double SurfaceSpeedVc { get; set; } = 0;
    }
}
