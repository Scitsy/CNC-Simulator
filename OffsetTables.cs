using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanucSimulator
{
    public class ToolOffset
    {
        public int Number { get; set; }
        public double XGeometry { get; set; }
        public double ZGeometry { get; set; }
        public double XWear { get; set; }
        public double ZWear { get; set; }
        public double NoseRadius { get; set; } = 0.4;
        public int NoseDirection { get; set; } = 3;

        // Populated by assigning a ToolCatalog entry, or edited directly - manual override always
        // works, same as a real machine's offset table. Geometry/wear above stay per-slot even
        // after a catalog assignment, since those come from physically touching the tool off.
        public ToolType Type { get; set; } = ToolType.Undefined;
        public InsertShape Insert { get; set; } = InsertShape.None;
        public string CatalogDesignation { get; set; } = "";
        public double Width { get; set; }
        public double ShankSize { get; set; }
        public double Overhang { get; set; }
        public GrooveReference Reference { get; set; } = GrooveReference.Center;
        public double InsertReach { get; set; }

        public double TotalX => XGeometry + XWear;
        public double TotalZ => ZGeometry + ZWear;

        public void AssignFromCatalog(CatalogEntry entry)
        {
            Type = entry.Type;
            Insert = entry.Insert;
            NoseRadius = entry.NoseRadius;
            Width = entry.Width;
            ShankSize = entry.ShankSize;
            Overhang = entry.Overhang;
            Reference = entry.Reference;
            InsertReach = entry.InsertReach;
            CatalogDesignation = $"{entry.InsertDesignation} / {entry.HolderDesignation}";
        }
    }

    public class WorkOffset
    {
        public int GCode { get; set; } // 54-59
        public double X { get; set; }
        public double Z { get; set; }
    }

    public class OffsetTables
    {
        public Dictionary<int, ToolOffset> Tools { get; } = new();
        public Dictionary<int, WorkOffset> WorkOffsets { get; } = new();

        public OffsetTables()
        {
            for (int i = 1; i <= MachineSpec.TurretStations; i++)
                Tools[i] = new ToolOffset { Number = i };

            // Sensible out-of-the-box defaults, matching the exact T1-T4 roles from the user's own
            // reference program (T1 OD/facing, T2 drill, T3 boring, T4 threading) so re-running it
            // doesn't trip a false tool-mismatch alarm against these defaults. Grooving goes in the
            // unused T5 slot rather than displacing T4. T8 up to the turret's last station stay Undefined.
            Tools[1].AssignFromCatalog(ToolCatalog.Entries[0]);  // CNMG120404 OD turning/finishing
            Tools[2].AssignFromCatalog(ToolCatalog.Entries[17]); // HSS-8.0 twist drill
            Tools[3].AssignFromCatalog(ToolCatalog.Entries[6]);  // CCMT09T304 boring, medium reach
            Tools[4].AssignFromCatalog(ToolCatalog.Entries[13]); // 16ER1.5ISO threading, metric
            Tools[5].AssignFromCatalog(ToolCatalog.Entries[10]); // MGMN300-M grooving, 3mm
            Tools[6].AssignFromCatalog(ToolCatalog.Entries[12]); // MGIVR150-M internal grooving (ID O-ring grooves)
            Tools[7].AssignFromCatalog(ToolCatalog.Entries[14]); // 16ER8UN threading, 60deg UN/UNC profile - closer lineage to NPT pipe thread than the ISO metric profile on T4

            for (int g = 54; g <= 59; g++)
                WorkOffsets[g] = new WorkOffset { GCode = g };
        }

        public ToolOffset GetOrCreateTool(int number)
        {
            if (!Tools.TryGetValue(number, out var offset))
            {
                offset = new ToolOffset { Number = number };
                Tools[number] = offset;
            }
            return offset;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // System.Text.Json doesn't populate get-only Dictionary properties from JSON (Tools/
        // WorkOffsets above stay read-only deliberately, so nothing outside this class can swap the
        // whole table out from under GetOrCreateTool) - so save/load goes through this flat DTO
        // instead of serializing OffsetTables directly.
        private class Dto
        {
            public List<ToolOffset> Tools { get; set; } = new();
            public List<WorkOffset> WorkOffsets { get; set; } = new();
        }

        // Tool and work offsets live in battery-backed memory on a real control - they survive a
        // power cycle without any explicit operator save. This mirrors that: silently persisted to
        // a fixed file, loaded back on startup, no user-facing save/load action required.
        public void SaveToFile(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var dto = new Dto
            {
                Tools = Tools.Values.OrderBy(t => t.Number).ToList(),
                WorkOffsets = WorkOffsets.Values.OrderBy(w => w.GCode).ToList()
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            File.WriteAllText(path, json);
        }

        // Falls back to fresh hardcoded defaults on first run, a missing file, or a corrupt/
        // unreadable one - persistence is a convenience, never a reason to fail to start.
        public static OffsetTables LoadOrDefault(string path)
        {
            if (!File.Exists(path))
                return new OffsetTables();

            try
            {
                var json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
                if (dto == null)
                    return new OffsetTables();

                var result = new OffsetTables();
                foreach (var tool in dto.Tools)
                    result.Tools[tool.Number] = tool;
                foreach (var work in dto.WorkOffsets)
                    result.WorkOffsets[work.GCode] = work;
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
            {
                return new OffsetTables();
            }
        }
    }
}
