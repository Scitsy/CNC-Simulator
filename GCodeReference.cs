using System.Collections.Generic;

namespace FanucSimulator
{
    public class SetupTip
    {
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public static class GCodeReference
    {
        public static readonly Dictionary<string, string> GCodes = new()
        {
            ["G00"] = "Rapid positioning. Moves the tool at maximum traverse speed with no cutting - used to get into position quickly without touching the part.",
            ["G01"] = "Linear interpolation (feed move). Moves the tool in a straight line at the programmed feed rate - this is your actual cutting move.",
            ["G02"] = "Circular interpolation, clockwise. Cuts an arc from the current position to X/Z around a center given as I/K (offset from the start point) or a radius R.",
            ["G03"] = "Circular interpolation, counter-clockwise. Same as G02 but the other direction.",
            ["G04"] = "Dwell. Pauses execution for a specified time (P = milliseconds, X = seconds) with the spindle still turning - used to clean up a corner, form a groove bottom, or break a chip.",
            ["G20"] = "Inch programming mode. All following X/Z/F values are interpreted in inches until G21 is used.",
            ["G21"] = "Metric programming mode (millimeters). This is the default and most common mode.",
            ["G28"] = "Return to reference (home) position - the fixed point at the positive extreme of both axes where the turret parks, used for tool changes and at the end of a program. Giving X/Z (or U/W) rapids via that intermediate point first, which is how a program clears a fixture before homing; a bare G28 goes straight there. Note this is NOT X0 Z0: on a lathe that would be the spindle centreline at the face, straight through the part.",
            ["G40"] = "Cancel tool nose radius compensation. The control stops offsetting the toolpath for the tool's nose radius - programmed coordinates are followed exactly.",
            ["G41"] = "Tool nose radius compensation, left. Offsets the toolpath to the left of the programmed path (looking in the direction of travel) by the tool's nose radius, keeping tapers and angles accurate.",
            ["G42"] = "Tool nose radius compensation, right. Same idea as G41 but offsets to the right of the programmed path.",
            ["G50"] = "With S: clamps the maximum spindle speed, protecting against excessive RPM when using G96 constant surface speed. With X/Z: presets the current position to a coordinate value without moving.",
            ["G54"] = "Work coordinate system 1. Stores an X/Z offset from machine zero to your part's zero point, so you can touch off once and program in convenient part coordinates.",
            ["G55"] = "Work coordinate system 2. Same purpose as G54, a separate stored offset - handy for multi-part setups or fixtures.",
            ["G56"] = "Work coordinate system 3.",
            ["G57"] = "Work coordinate system 4.",
            ["G58"] = "Work coordinate system 5.",
            ["G59"] = "Work coordinate system 6.",
            ["G70"] = "Finishing cycle. Follows a G71/G72 roughing pass - replays the same P/Q sequence-number range at finishing feed with no roughing allowance, cutting the exact finish contour.",
            ["G71"] = "Roughing cycle, turning. Setup block (U=depth per pass, R=retract) then a trigger block (P/Q=sequence numbers bounding the finish contour, U/W=finish allowance in X/Z). Repeatedly removes stock stepping in X down to the finish contour, assumes external OD turning (stock outside the contour).",
            ["G72"] = "Roughing cycle, facing. Same idea as G71 but steps in Z (setup block uses W for depth per pass) while walking the contour in X.",
            ["G74"] = "Peck drilling cycle. Setup block (R=retract) then a trigger block (X/Z=bore end point, Q=Z peck depth, P=optional X shift between peck runs for stepping to another diameter, both in microns) - pecks toward the target Z with a retract between pecks for chip breaking, then fully retracts to the start Z. The carved hole diameter comes from the drill tool's own fixed geometry, not the programmed X.",
            ["G75"] = "Grooving cycle. Setup block (R=retract) then a trigger block (X/Z=groove floor target, P=X peck increment, Q=Z step for wide grooves, both in microns) - pecks toward the target with a retract between pecks for chip breaking.",
            ["G76"] = "Threading cycle. Setup block (P=encoded finish-pass-count/chamfer/tip-angle, Q=minimum depth of cut) then a trigger block (X/Z=thread end point, R=taper, P=thread height, Q=first cut depth, F=lead) - cuts multiple passes with depth decreasing pass to pass (equal cutting area), finishing with spring passes at full depth.",
            ["G65"] = "Custom macro call. P=the O-number of the macro to run, L=repeat count (default 1), plus argument letters (A-Z, mostly - see the Custom Macro B Basics tip) that get bound to that macro's own local variables (#1-#33) for the duration of the call. Unlike M98, each G65 call gets a fresh, isolated set of local variables, and calls can nest (a macro can call another macro).",
            ["G66"] = "Modal custom macro call. Same P/L/argument-letter syntax as G65, but instead of calling once, arms the macro to automatically run again before every subsequent block that commands axis motion (X/Z/etc.), using the same arguments each time - handy for repeating a canned sub-routine (a peck cycle, a chamfer pass) at a series of programmed positions without a G65 on every line. Stays active until G67. Does not fire before G71/G72/G75/G76 setup or trigger blocks.",
            ["G67"] = "Cancel modal custom macro call (G66). The armed macro stops auto-firing; subsequent motion blocks behave normally again.",
            ["G80"] = "Cancel canned cycle. Turns off an active single cycle (G90/G92/G94), returning coordinate blocks to ordinary moves. Commonly included defensively at program start. Does not affect G70-G76, which aren't modal - they run once per trigger block.",
            ["G90"] = "OD/ID turning cycle (single canned cycle). 'G90 X_ Z_ F_' cuts a straight pass to diameter X over length Z and returns to the start point: rapid in X, feed along Z, feed out in X, rapid back in Z. Add R for a taper (a signed radius value). It is modal - once armed, a following block giving only a new X repeats the cycle at that depth, which is how successive roughing passes are programmed. Cancel with G80. NOTE: G90 means absolute positioning only in G-code systems B and C; this control uses system A, where absolute/incremental is X/Z vs U/W instead.",
            ["G92"] = "Thread cutting cycle (single canned cycle). 'G92 X_ Z_ F_' cuts one thread pass to diameter X over length Z at lead F, then retracts and returns to the start point. Repeat blocks with decreasing X to take successive passes. Add R for a tapered thread. Modal, cancelled by G80. Requires a threading tool.",
            ["G94"] = "End face turning cycle (single canned cycle). The facing counterpart of G90: 'G94 X_ Z_ F_' rapids in Z, feeds inward in X to face to diameter X, then returns to the start point. Add R to taper along Z. Modal, cancelled by G80.",
            ["G32"] = "Single-block thread cutting. Cuts one constant-lead thread pass along the commanded vector, with F as the lead (distance per spindle revolution) rather than a feed rate. Unlike G92 there is no cycle wrapper - the program handles its own infeed and retract. G33 is accepted as an alias.",
            ["G96"] = "Constant surface speed (CSS). The control continuously adjusts spindle RPM as the tool moves in X so cutting speed at the tip stays constant (S = surface speed, e.g. m/min) - keeps finish and tool life consistent as diameter changes.",
            ["G97"] = "Constant spindle speed. Cancels CSS - the spindle runs at the RPM you specify with S, regardless of diameter.",
            ["G98"] = "Feed per minute. The F value is interpreted as distance per minute.",
            ["G99"] = "Feed per revolution. The F value is interpreted as distance per spindle revolution - the common default for turning since it keeps chip load consistent regardless of RPM.",
        };

        public static readonly Dictionary<string, string> MCodes = new()
        {
            ["M00"] = "Program stop. Pauses the program completely - press Cycle Start (Execute) to continue. Often used before an inspection or manual step.",
            ["M01"] = "Optional stop. Same as M00 but only pauses if the operator has enabled the Optional Stop switch on the control - not modeled here, so this always just logs and continues.",
            ["M02"] = "Program end. Stops the program where it stands; unlike M30 it does not rewind the cursor back to the top.",
            ["M19"] = "Spindle orient. Stops the spindle at a fixed angular position, used before a tool change or a driven-tool operation.",
            ["M03"] = "Spindle on, forward (clockwise, viewed from the tailstock). S sets the speed.",
            ["M04"] = "Spindle on, reverse (counter-clockwise).",
            ["M05"] = "Spindle stop.",
            ["M06"] = "Tool change. Selects the tool and offset number from the T-code (e.g. T0101 = tool 1, offset 1).",
            ["M08"] = "Coolant on.",
            ["M09"] = "Coolant off.",
            ["M30"] = "Program end and rewind. Stops the program and resets the block pointer back to the start.",
            ["M98"] = "Call subprogram. P is the subprogram (O-number) to run, L is how many times to repeat it. Calls can nest (a subprogram can call another subprogram, or a G65 macro) up to 8 levels deep. Unlike G65, a called subprogram shares the caller's local variables (#1-#33) rather than getting its own fresh set.",
            ["M99"] = "Return from subprogram or macro. Marks the end of a subprogram/macro body and sends control back to whatever called it (M98 or G65).",
        };

        public static readonly List<SetupTip> SetupTips = new()
        {
            new SetupTip
            {
                Title = "G-Code System A (and why there is no G90/G91 here)",
                Body = "This control uses FANUC G-code system A, the lathe default. That has one consequence people coming from a machining center trip over constantly: absolute vs incremental is NOT selected by G90/G91. Instead it is chosen per address - X and Z are always absolute, U and W are always incremental, and you can mix them in the same block ('G01 X20 W-5' means absolute diameter 20, five further along Z). If a block gives both for one axis, the incremental address wins. In system A, G90/G92/G94 are instead the three single canned cycles (OD/ID turning, thread cutting, end face turning). Systems B and C are the ones where G90/G91 mean absolute/incremental - if you paste a program written for one of those, its G90/G91 lines will not do what you expect. Codes this control does not support now raise an alarm rather than being silently ignored, so a mistyped or unsupported code shows up immediately instead of quietly doing nothing."
            },
            new SetupTip
            {
                Title = "Auxiliary M-Codes Are Machine-Specific (these are GUESSES)",
                Body = "FANUC defines the common M-codes - M00/M01/M02/M30 program control, M03/M04/M05 spindle, M08/M09 coolant, M19 orient, M98/M99 subprograms - and those mean the same thing on every FANUC lathe. Everything else (chuck clamp/unclamp, tailstock quill, parts catcher, wash gun, chip conveyor) is assigned by the machine BUILDER in the machine's ladder, so the numbers differ from machine to machine. The auxiliary codes this simulator accepts - M10/M11 chuck, M12/M13 tailstock, M21/M22 parts catcher, M50/M51 wash gun, M52/M53 conveyor - were inferred from the reference machine's operator panel, which shows the functions but not their numbers. TREAT THEM AS PLACEHOLDERS: they are very unlikely to match your machine. Check your machine's own M-code list and correct them (they live in one table in LatheSimulator.cs). Running an unverified auxiliary M-code on real iron is how people crash chucks."
            },
            new SetupTip
            {
                Title = "Finding Your Part's Center / Zero",
                Body = "On a lathe, X0 is the spindle centerline and Z0 is usually the finished face of the part. To set Z0, touch the tool tip lightly to the face and zero Z in your work offset (G54 etc.) at that point. X0 is rarely touched off directly - most shops face or turn a known diameter, measure it with calipers, then dial the difference into the work offset or tool wear offset until the DRO matches. When boring or facing near center, a full continuous witness mark across the face (no step or unfinished nub) confirms you're on true center."
            },
            new SetupTip
            {
                Title = "Tool Setup Basics",
                Body = "Mount tools at (or very close to) on-center height - too high or low changes the effective rake and clearance angles and can cause rubbing or poor finish, especially facing near center. Keep overhang as short as practical to reduce deflection and chatter. After mounting, measure and enter each tool's geometry offset (X/Z) in the OFFSET table so the control knows exactly where that tool's tip sits relative to the reference tool, letting you swap tools mid-program without losing part coordinates."
            },
            new SetupTip
            {
                Title = "Reducing Chatter",
                Body = "Chatter - that harsh buzzing sound and rippled finish - is usually too much tool overhang, insufficient rigidity, or a feed/speed combination exciting a resonance. Try: shortening tool overhang, changing spindle RPM up or down slightly to 'detune' the resonance, increasing feed rate a bit (a heavier chip load is sometimes more stable than a lighter one), and checking for loose tooling or a workpiece that isn't rigidly held. A boring bar chattering in a deep bore is one of the most common cases."
            },
            new SetupTip
            {
                Title = "Boring Bar Technique",
                Body = "Boring bars are inherently less rigid than OD tools because they're long and thin relative to the cutting force involved, so chatter and deflection are the main enemies. Use the largest-diameter, shortest-overhang bar that fits the bore - carbide-shank or solid-carbide bars resist deflection far better than steel for deep bores. Reduce depth of cut and feed compared to an equivalent OD cut, favor a sharper/more positive insert geometry, and peck long passes to clear chips so a packed bore doesn't push the bar off-center."
            },
            new SetupTip
            {
                Title = "Feeds & Speeds Basics",
                Body = "Cutting speed (surface speed, e.g. m/min) is a property of the material/tool pairing, not the machine - that's why G96 exists, so RPM automatically adjusts as diameter changes. Feed rate (mm/rev is standard for turning) controls chip thickness: too low and you rub instead of cut (poor tool life, work hardening on stainless/superalloys); too high risks poor finish or a broken insert. Start near the insert manufacturer's recommended values for the material, then adjust feed first for more material removal rate - it's generally gentler on tool life than pushing speed too hard."
            },
            new SetupTip
            {
                Title = "Tool Compensation Explained",
                Body = "Two different offsets live in the OFFSET table and are easy to mix up: geometry offset defines where the tool tip physically is relative to the machine's reference position, set once during tool setup; wear offset is a small adjustment dialed in during production to correct wear or fine-tune a dimension, without touching geometry. Separately, tool nose radius compensation (G41/G42/G40) corrects for the fact that a turning insert has a rounded tip, not a perfect point - without it, tapers, chamfers, and radii come out slightly undersized or oversized."
            },
            new SetupTip
            {
                Title = "Reading Insert & Holder Designations (ISO 1832 / ISO 5608)",
                Body = "Insert codes like CNMG120408 follow ISO 1832: position 1 is shape (C=80deg diamond, D=55deg diamond, V=35deg diamond, T=60deg triangle, S=90deg square, R=round, W=80deg trigon), 2 is clearance/relief angle, 3 is tolerance class, 4 is fixing/chipbreaker style, then a 2-digit inscribed-circle size, a 2-digit thickness, and a 2-digit nose radius in tenths of a millimeter (08 = 0.8mm - roughing geometries run a larger radius than finishing ones, like 04 = 0.4mm, since a bigger radius spreads cutting force but leaves a coarser scallop at a given feed). Holder codes like MCLNR2525M12 follow ISO 5608 (or the equivalent boring-bar convention): the leading letter usually matches the insert shape, followed by clamping method, hand of cut, shank height/width (or bar diameter for boring bars, e.g. S25S-SCLCR09 = 25mm bar), and overall length. Shank size and overhang are exactly the rigidity numbers behind the Reducing Chatter and Boring Bar Technique tips - a bigger shank or shorter overhang means less deflection. The TOOL CATALOG on the OFFSET screen holds ready-made holder+insert combinations built from these two standards; assigning one to a tool number copies its type, insert shape, nose radius, width, shank size, and overhang into that slot's offset row."
            },
            new SetupTip
            {
                Title = "Custom Macro B Basics",
                Body = "Variables (#1-#33 local, reset for each G65 macro call; #100-#999 common, shared everywhere) hold a number you can read or write anywhere a normal value would go - #0 is always empty/null. A small set of read-only system variables are also available: #5001/#5002 read the current work-coordinate X/Z position, and #4001 reads the active motion modal group (0/1/2/3 for G00/G01/G02/G03) - handy for a macro that needs to know where it is or how it's about to move without the caller passing it in as an argument. Assign with '#1=5' or '#1=[#2+3]' (writing to a system variable raises an alarm - they're read-only); the right side of an assignment, and any address word's value (X[#1+5], Z#2), must be a plain number, a bare #variable, or exactly one square-bracketed expression - round parens stay reserved for comments, so grouping uses [ ] instead: [[#1+#2]*2]. Inside brackets you get + - * / MOD, unary minus, and the functions SIN/COS/TAN/ATAN/SQRT/ABS/ROUND/FIX/FUP (trig in degrees). Branch with 'IF[condition] GOTO n' (jumps to the block carrying Nn if true, otherwise falls through) or a bare 'GOTO n'; conditions compare two expressions with EQ/NE/GT/LT/GE/LE (e.g. IF[#1 GE 10] GOTO 50). Chain multiple comparisons with AND/OR, each in its own brackets: IF[#1 GT 0] AND[#2 LT 10] GOTO 50, or WHILE[#1 LT 10] OR[#2 EQ 0] DO1 - evaluated strictly left to right, no precedence between AND and OR (so mixing them needs care about order). Loop with 'WHILE[condition] DO1 ... END1' (matching DO/END numbers, 1-3, let you nest up to three loops) - the block re-checks the condition every time it reaches DO. Call a macro with G65 P(O-number) L(repeat) plus argument letters - A/B/C/I/J/K/D/E/F/H/Q/R/S/T/U/V/W/X/Y/Z each map to a fixed local variable (#1-#26ish, skipping a few) inside the called macro, so 'G65 P9010 X10 Z-5' hands the macro X as #24 and Z as #26. Unlike M98 (which shares the caller's locals), every G65 call gets its own fresh #1-#33, and calls can nest - a macro can call another macro. G66 (same P/L/argument syntax as G65) arms a modal version instead of calling once - the macro then auto-runs, with those same arguments, before every later block that moves an axis, until G67 cancels it - handy for repeating a sub-routine at a series of programmed positions without a G65 on every line."
            },
        };
    }
}
