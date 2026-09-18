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

        // Rapid traverse, both axes. Confirmed by the machine's owner (2026-09-18) - this machine's
        // rapid is 500 in/min. Not published anywhere the earlier spec research found; it lives in
        // the control's own parameter 1420. Replaces an invented 10 m/min placeholder that was about
        // 21% slow, which never mattered much while programs ran instantly but is on screen once the
        // tool is seen moving.
        //
        // Path, confirmed by the owner (2026-09-18): X and Z in the same block rapid together in a
        // straight line to the end point (parameter 1401 bit 1, LRP=1 - not a 45-degree "dogleg");
        // an axis in the next block moves on its own after the first arrives. The move takes as
        // long as its longer axis needs at this rate.
        public const double RapidTraverseInchesPerMin = 500;
        public const double RapidTraverseMmPerMin = RapidTraverseInchesPerMin * 25.4;

        // Spindle ramp: seconds from stopped to MaxSpindleRpm. NOT measured on this machine - a
        // typical figure for a spindle this size, chosen with the owner (2026-09-18) as a starting
        // point to replace once the real machine is timed. Adjustable on the SYSTEM screen.
        public const double SpindleRampSecondsTypical = 3.0;

        // Turret. Twelve physical pockets, servo-indexed, but the tool holders are double-sided
        // (confirmed by the machine's owner, 2026-09-01) - each pocket mounts two tools front/back,
        // so the T-word addresses 24 positions, matching the panel's TURRET dial (which reads 1-24,
        // not 1-12). TurretStations is the ADDRESSABLE count - what OffsetTables sizes itself to and
        // what T-words actually select - not the physical pocket count, which is kept separately
        // below purely for documentation.
        public const int TurretStations = 24;
        public const int TurretPhysicalPockets = 12;

        // Chuck and capacity, in inches - this machine is an inch machine (see ModalState.Units).
        public const double ChuckSizeInches = 8.0;
        public const double BarCapacityInches = 2.5;
        public const double MaxTurningDiameterInches = 14.96;
        public const double MaxTurningLengthInches = 19.7;

        public static string Nameplate => $"{Builder} {Model}  -  {Control}";
    }
}
