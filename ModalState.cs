using System.Collections.Generic;

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

        // Powers on in inch, matching the reference machine (its screen reads INCH/M and carries G20
        // in the modal block). A program that declares G20/G21 for itself is unaffected either way -
        // this is only the state before anything sets it, plus MDI.
        public UnitsMode Units { get; set; } = UnitsMode.Inch;
        public FeedMode Feed { get; set; } = FeedMode.PerRevolution;
        public SpindleMode Spindle { get; set; } = SpindleMode.ConstantRpm;
        public CutterComp Comp { get; set; } = CutterComp.Off;
        public CannedCycle Cycle { get; set; } = CannedCycle.None;
        public int ActiveWorkOffset { get; set; } = 54;
        public double? MaxCssRpm { get; set; }
        public double SurfaceSpeedVc { get; set; } = 0;

        // Modal groups the real control carries and displays but which have no effect on a 2-axis
        // turning simulation - their active members need a C axis, tool length compensation, or a
        // servo/look-ahead model none of which exist here. They are tracked rather than hardcoded
        // so the modal block shows what the program actually last commanded: accepting G23 and then
        // displaying G22 anyway would be a lie on screen. Defaults are each group's cancel state,
        // which is what the reference machine's screen shows.
        public int Plane { get; set; } = 18;                  // G17 / G18 (ZX, the lathe default) / G19
        public int StrokeCheck { get; set; } = 22;            // G22 on / G23 off
        public int SpeedFluctuationDetect { get; set; } = 25; // G25 off / G26 on
        public int CuttingMode { get; set; } = 64;            // G61 exact stop / G64 cutting
        public int PolarCommand { get; set; } = 15;           // G15 cancel / G16 on
        public int CoordRotation { get; set; } = 69;          // G69 cancel / G68 on
        public int ToolLengthComp { get; set; } = 49;         // G49 cancel

        // Same idea for the decimal-suffixed groups (G13.1, G50.1, G40.1 ...), keyed by group name
        // because the codes themselves are text - see LatheSimulator's ExtendedCodeGroups.
        public Dictionary<string, string> ExtendedGroups { get; } = new()
        {
            ["PolarInterpolation"] = "G13.1",
            ["MirrorImage"] = "G50.1",
            ["NormalDirection"] = "G40.1",
            ["PolygonTurning"] = "G50.2",
            ["BalancedCutting"] = "G69.1",
            ["HighSpeedCycle"] = "G05.5",
            ["ElectronicGearBoxA"] = "G80.4",
            ["ElectronicGearBoxB"] = "G80.5",
        };
    }
}
