namespace FanucSimulator
{
    // The specific machine this simulator is modelled on, supplied by its owner on 2026-08-25:
    //
    //     Leadwell LTC-208 turning centre, serial L2TAG0843, manufactured 2020,
    //     FANUC Series 0i-TF Plus control.
    //
    // Everything in this file is a published specification of that machine, not an invention. It is
    // kept apart from the engine's own limits (LatheSimulator.MaxX/MaxZ/MinZ, which are simulator
    // workspace bounds) so that "what the real machine can do" and "what this program allows" never
    // get quietly confused with one another.
    //
    // Sources are secondhand - dealer listings and the builder's product literature, since Leadwell
    // does not publish the LTC-208 manual openly. They agree with each other, but if a figure here
    // ever contradicts the machine's own nameplate or manual, the machine wins.
    public static class MachineSpec
    {
        public const string Builder = "LEADWELL";
        public const string Model = "LTC-208";
        public const string Serial = "L2TAG0843";
        public const string ManufacturedYear = "2020";
        public const string Control = "FANUC Series 0i-TF Plus";

        // Spindle. Commanding more than this does not fault a real control - it silently clamps,
        // which is why the engine clamps rather than alarming. Listings for the 0i-TF Plus LTC-208
        // quote 4500 rpm; some older LTC-208 literature quotes 4000. The higher figure is used
        // because it matches this machine's control generation, and because clamping low would
        // invent a restriction the machine does not have.
        public const double MaxSpindleRpm = 4500;

        // Turret. Twelve stations, servo-indexed. NOTE: the TURRET selector on the reference
        // photo of this machine's operator panel is graduated 1-24 - Leadwell fits a common panel
        // across models with larger turrets. The station count below follows the machine's
        // specification, not the dial's printed range.
        public const int TurretStations = 12;

        // Chuck and capacity, in inches - this machine is an inch machine (see ModalState.Units).
        public const double ChuckSizeInches = 8.0;
        public const double BarCapacityInches = 2.5;
        public const double MaxTurningDiameterInches = 14.96;
        public const double MaxTurningLengthInches = 19.7;

        public static string Nameplate => $"{Builder} {Model}  -  {Control}";
    }
}
