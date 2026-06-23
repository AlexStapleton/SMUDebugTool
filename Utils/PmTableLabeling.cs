using System.Collections.Generic;
using System.Globalization;

namespace ZenStatesDebugTool
{
    // A WinForms-free mirror of one ZenStates SmuSensorDefinition entry.
    public struct SensorInfo
    {
        public readonly string Name;
        public readonly float Scale;
        public SensorInfo(string name, float scale) { Name = name; Scale = scale; }
    }

    public sealed class LabeledRow
    {
        public int Index { get; set; }
        public uint Offset { get; set; }
        public float Raw { get; set; }
        public string Name { get; set; }   // "" when the offset is unlabeled
        public string Scaled { get; set; } // "" when the offset is unlabeled
    }

    public static class PmTableLabeling
    {
        // Pairs each table slot with its sensor name/scaled value when the structure
        // defines it; blank otherwise. ZenStates' GetPmTableStructure keys sensors by
        // float-array element index (not byte offset), so the lookup uses the index;
        // Offset (index*4) is the byte offset kept for display only.
        public static List<LabeledRow> Label(float[] table, IReadOnlyDictionary<uint, SensorInfo> structure)
        {
            var rows = new List<LabeledRow>();
            if (table == null) return rows;

            for (int i = 0; i < table.Length; i++)
            {
                var row = new LabeledRow
                {
                    Index = i,
                    Offset = (uint)(i * 4),
                    Raw = table[i],
                    Name = "",
                    Scaled = "",
                };

                if (structure != null && structure.TryGetValue((uint)i, out SensorInfo info))
                {
                    row.Name = info.Name ?? "";
                    row.Scaled = FormatScaled(table[i], info);
                }

                rows.Add(row);
            }
            return rows;
        }

        // Single source of truth for the scaled-value formatting, so the WinForms
        // PMTable grid and this pure helper can't drift apart.
        public static string FormatScaled(float raw, SensorInfo info)
            => (raw * info.Scale).ToString("F3", CultureInfo.InvariantCulture);
    }
}
