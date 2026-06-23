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
        // Pairs each table slot (offset = index*4) with its sensor name/scaled
        // value when the structure defines it; blank otherwise.
        public static List<LabeledRow> Label(float[] table, IReadOnlyDictionary<uint, SensorInfo> structure)
        {
            var rows = new List<LabeledRow>();
            if (table == null) return rows;

            for (int i = 0; i < table.Length; i++)
            {
                uint offset = (uint)(i * 4);
                var row = new LabeledRow
                {
                    Index = i,
                    Offset = offset,
                    Raw = table[i],
                    Name = "",
                    Scaled = "",
                };

                if (structure != null && structure.TryGetValue(offset, out SensorInfo info))
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
