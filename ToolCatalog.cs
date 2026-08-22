using System.Collections.Generic;

namespace FanucSimulator
{
    public enum ToolType { Undefined, OdTurning, IdBoring, Grooving, IdGrooving, Threading, Drill }
    public enum InsertShape { Diamond80, Diamond55, Diamond35, Triangle60, Round, Square, None }

    // Where a grooving/threading blade's programmed X/Z sits relative to its own width - only
    // meaningful for Grooving/Threading. Matters when stepping the tool sideways to widen a groove:
    // the operator needs to know whether the program point tracks the blade's left flank, right
    // flank, or center. Center (symmetric about the programmed point) was the previous implicit
    // default.
    public enum GrooveReference { Center, Left, Right }

    // A catalog entry models a real tool as an assembly of holder + insert, the way a CAM tool
    // library does - it's a reusable template, not a machine slot. Assigning one to a T-number
    // offset (OffsetTables.ToolOffset) copies these static properties in; geometry/wear offsets
    // stay per-slot since those come from physically touching the tool off, not the catalog.
    //
    // Designations follow the open ISO 1832 (insert) and ISO 5608 (holder) standard structure -
    // e.g. "CNMG120408" decodes as 80-degree diamond / 0-degree clearance / M tolerance / G
    // fixing-chipbreaker / 12(size) / 04(thickness) / 08 (0.8mm nose radius). These are
    // representative generic examples built from the public standard, not real manufacturer part
    // numbers - see the HELP screen for the full designation explanation. Structural inspiration
    // (not copied data) drawn from open-source CNC tool-library projects: FreeCAD's Path
    // Workbench/Better Tool Library and the community LibLathe/TurningAddon.
    public class CatalogEntry
    {
        public string InsertDesignation { get; set; } = "";
        public string HolderDesignation { get; set; } = "";
        public string Description { get; set; } = "";
        public ToolType Type { get; set; }
        public InsertShape Insert { get; set; }
        public double NoseRadius { get; set; }
        public double Width { get; set; }      // groove blade width (mm) or drill diameter (mm); 0 if unused
        public double ShankSize { get; set; }  // holder shank height/width (turning) or bar diameter (boring), mm
        public double Overhang { get; set; }   // holder length / boring bar reach, mm
        public GrooveReference Reference { get; set; } = GrooveReference.Center;

        // How far the blade/insert extends from the holder along the plunge (X) axis - only
        // meaningful for Grooving/Threading, where it's the real limiting factor on max groove depth
        // (or thread engagement length). Unused (0 if unused) for turning/boring/drill, whose reach
        // is already captured by Overhang instead.
        public double InsertReach { get; set; }
    }

    public static class ToolCatalog
    {
        public static readonly List<CatalogEntry> Entries = new()
        {
            // ---- OD turning/facing (external, diamond inserts) ----
            new CatalogEntry
            {
                InsertDesignation = "CNMG120404", HolderDesignation = "MCLNR2525M12",
                Description = "OD turning/facing, 80deg diamond, 0.4mm nose R - finishing, 25mm shank",
                Type = ToolType.OdTurning, Insert = InsertShape.Diamond80,
                NoseRadius = 0.4, ShankSize = 25, Overhang = 40
            },
            new CatalogEntry
            {
                InsertDesignation = "CNMG120408", HolderDesignation = "MCLNR2525M12",
                Description = "OD turning/facing, 80deg diamond, 0.8mm nose R - roughing, 25mm shank",
                Type = ToolType.OdTurning, Insert = InsertShape.Diamond80,
                NoseRadius = 0.8, ShankSize = 25, Overhang = 40
            },
            new CatalogEntry
            {
                InsertDesignation = "DNMG150604", HolderDesignation = "DCLNR2525M15",
                Description = "OD profiling, 55deg diamond - flexible for contours/tapers, 25mm shank",
                Type = ToolType.OdTurning, Insert = InsertShape.Diamond55,
                NoseRadius = 0.4, ShankSize = 25, Overhang = 40
            },
            new CatalogEntry
            {
                InsertDesignation = "VNMG160404", HolderDesignation = "SVJBR2525M16",
                Description = "OD finishing, 35deg diamond - pointed profile/copy turning, 25mm shank",
                Type = ToolType.OdTurning, Insert = InsertShape.Diamond35,
                NoseRadius = 0.4, ShankSize = 25, Overhang = 40
            },
            new CatalogEntry
            {
                InsertDesignation = "TNMG160404", HolderDesignation = "MTJNR2525M16",
                Description = "OD turning, 60deg triangle - general purpose, compact, 25mm shank",
                Type = ToolType.OdTurning, Insert = InsertShape.Triangle60,
                NoseRadius = 0.4, ShankSize = 25, Overhang = 40
            },

            // ---- ID boring (round shank bars, at increasing reach - the chatter tradeoff the
            // HELP tips describe: shorter/fatter bar is more rigid, longer reach risks chatter) ----
            new CatalogEntry
            {
                InsertDesignation = "CCMT060204", HolderDesignation = "S16Q-SCLCR06",
                Description = "ID boring, short reach, 16mm bar dia - most rigid, shallow bores only",
                Type = ToolType.IdBoring, Insert = InsertShape.Diamond80,
                NoseRadius = 0.4, ShankSize = 16, Overhang = 100
            },
            new CatalogEntry
            {
                InsertDesignation = "CCMT09T304", HolderDesignation = "S25S-SCLCR09",
                Description = "ID boring, medium reach, 25mm bar dia - typical general-purpose bore",
                Type = ToolType.IdBoring, Insert = InsertShape.Diamond80,
                NoseRadius = 0.4, ShankSize = 25, Overhang = 175
            },
            new CatalogEntry
            {
                InsertDesignation = "CCMT120404", HolderDesignation = "S32U-SCLCR12",
                Description = "ID boring, deep reach, 32mm bar dia - watch for chatter at full extension",
                Type = ToolType.IdBoring, Insert = InsertShape.Diamond80,
                NoseRadius = 0.4, ShankSize = 32, Overhang = 260
            },
            new CatalogEntry
            {
                InsertDesignation = "CCMT06T104", HolderDesignation = "S12M-SCLCR06",
                Description = "ID boring, small-bore carbide-shank bar, 12mm dia - stiffer than steel for its size",
                Type = ToolType.IdBoring, Insert = InsertShape.Diamond80,
                NoseRadius = 0.4, ShankSize = 12, Overhang = 90
            },

            // ---- Grooving/parting (blade width is the key dimension) ----
            new CatalogEntry
            {
                InsertDesignation = "MGMN200-M", HolderDesignation = "MGEHR2525-2",
                Description = "External grooving, 2mm blade width, 25mm shank",
                Type = ToolType.Grooving, Insert = InsertShape.None,
                NoseRadius = 0.1, Width = 2.0, ShankSize = 25, Overhang = 32, InsertReach = 5.0
            },
            new CatalogEntry
            {
                InsertDesignation = "MGMN300-M", HolderDesignation = "MGEHR2525-3",
                Description = "External grooving/parting, 3mm blade width, 25mm shank",
                Type = ToolType.Grooving, Insert = InsertShape.None,
                NoseRadius = 0.15, Width = 3.0, ShankSize = 25, Overhang = 32, InsertReach = 6.0
            },
            new CatalogEntry
            {
                InsertDesignation = "MGMN400-M", HolderDesignation = "MGEHR2525-4",
                Description = "External parting, 4mm blade width, deeper parting capacity, 25mm shank",
                Type = ToolType.Grooving, Insert = InsertShape.None,
                NoseRadius = 0.2, Width = 4.0, ShankSize = 25, Overhang = 32, InsertReach = 15.0
            },
            new CatalogEntry
            {
                InsertDesignation = "MGIVR150-M", HolderDesignation = "S20R-MGIVR15",
                Description = "Internal grooving, 1.5mm blade width, 20mm bar dia",
                Type = ToolType.IdGrooving, Insert = InsertShape.None,
                NoseRadius = 0.1, Width = 1.5, ShankSize = 20, Overhang = 120, InsertReach = 4.0
            },

            // ---- Threading ----
            new CatalogEntry
            {
                InsertDesignation = "16ER1.5ISO", HolderDesignation = "SER2525M16",
                Description = "External threading, 60deg ISO metric profile, 25mm shank",
                Type = ToolType.Threading, Insert = InsertShape.None,
                NoseRadius = 0.05, ShankSize = 25, Overhang = 40, InsertReach = 5.0
            },
            new CatalogEntry
            {
                InsertDesignation = "16ER8UN", HolderDesignation = "SER2525M16",
                Description = "External threading, 60deg UN/UNC profile, 25mm shank",
                Type = ToolType.Threading, Insert = InsertShape.None,
                NoseRadius = 0.05, ShankSize = 25, Overhang = 40, InsertReach = 5.0
            },
            new CatalogEntry
            {
                InsertDesignation = "16IR1.5ISO", HolderDesignation = "S20T-SIR2516",
                Description = "Internal threading, 60deg ISO metric profile, 20mm bar dia",
                Type = ToolType.Threading, Insert = InsertShape.None,
                NoseRadius = 0.05, ShankSize = 20, Overhang = 100, InsertReach = 4.0
            },

            // ---- Drilling (diameter is the key dimension, no nose-radius/insert-shape concept) ----
            new CatalogEntry
            {
                InsertDesignation = "A6.3", HolderDesignation = "Center drill 60deg",
                Description = "Center drill, 6.3mm, for starting a hole on center before drilling",
                Type = ToolType.Drill, Insert = InsertShape.None,
                Width = 6.3, ShankSize = 6.3, Overhang = 50
            },
            new CatalogEntry
            {
                InsertDesignation = "HSS-8.0", HolderDesignation = "Straight shank twist drill",
                Description = "Twist drill, 8mm diameter, general purpose",
                Type = ToolType.Drill, Insert = InsertShape.None,
                Width = 8.0, ShankSize = 8.0, Overhang = 90
            },
            new CatalogEntry
            {
                InsertDesignation = "HSS-12.0", HolderDesignation = "Straight shank twist drill",
                Description = "Twist drill, 12mm diameter, general purpose",
                Type = ToolType.Drill, Insert = InsertShape.None,
                Width = 12.0, ShankSize = 12.0, Overhang = 110
            },
            new CatalogEntry
            {
                InsertDesignation = "WCMX0602-20", HolderDesignation = "U-drill 20mm 2xD",
                Description = "Indexable insert drill, 20mm diameter, for larger-diameter roughing holes",
                Type = ToolType.Drill, Insert = InsertShape.None,
                Width = 20.0, ShankSize = 20.0, Overhang = 130
            },
        };
    }
}
