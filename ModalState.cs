namespace FanucSimulator
{
    public enum MotionMode { Rapid, Linear, ArcCw, ArcCcw }
    public enum UnitsMode { Metric, Inch }
    public enum PositionMode { Absolute, Incremental }
    public enum FeedMode { PerMinute, PerRevolution }
    public enum SpindleMode { ConstantRpm, ConstantSurfaceSpeed }
    public enum CutterComp { Off, Left, Right }

    // Modal state persists across blocks until explicitly changed by another code in the same group.
    public class ModalState
    {
        public MotionMode Motion { get; set; } = MotionMode.Rapid;
        public UnitsMode Units { get; set; } = UnitsMode.Metric;
        public PositionMode Position { get; set; } = PositionMode.Absolute;
        public FeedMode Feed { get; set; } = FeedMode.PerRevolution;
        public SpindleMode Spindle { get; set; } = SpindleMode.ConstantRpm;
        public CutterComp Comp { get; set; } = CutterComp.Off;
        public int ActiveWorkOffset { get; set; } = 54;
        public double? MaxCssRpm { get; set; }
        public double SurfaceSpeedVc { get; set; } = 0;
    }
}
