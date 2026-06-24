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

        // Builds an index->SensorInfo map from named byte offsets (ZenStates' PTDef
        // stores FCLK/MCLK/voltages etc. as byte offsets into the table). Element
        // index = byteOffset / 4; negative offsets mean "not present" and are skipped;
        // the first label wins if two map to the same index. Scale is 1.0 (the stored
        // float is already the value).
        public static Dictionary<uint, SensorInfo> BuildNamedOffsetMap(
            IEnumerable<KeyValuePair<string, int>> namedByteOffsets)
        {
            var map = new Dictionary<uint, SensorInfo>();
            if (namedByteOffsets == null) return map;

            foreach (var entry in namedByteOffsets)
            {
                if (entry.Value < 0) continue;
                uint index = (uint)(entry.Value / 4);
                if (!map.ContainsKey(index))
                    map[index] = new SensorInfo(entry.Key, 1.0f);
            }
            return map;
        }

        // Single source of truth for the scaled-value formatting, so the WinForms
        // PMTable grid and this pure helper can't drift apart.
        public static string FormatScaled(float raw, SensorInfo info)
            => (raw * info.Scale).ToString("F3", CultureInfo.InvariantCulture);
    }
}
