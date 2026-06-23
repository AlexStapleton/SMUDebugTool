using System.Collections.Generic;
using System.Globalization;

namespace ZenStatesDebugTool
{
    // A WinForms-free projection of one ZenStates SmuSensorDefinition.
    public struct SensorInfo
    {
        public string Name;
        public float Scale;
        public SensorInfo(string name, float scale) { Name = name; Scale = scale; }
    }

    public sealed class LabeledRow
    {
        public int Index;
        public uint Offset;
        public float Raw;
        public string Name;   // "" when the offset is unlabeled
        public string Scaled; // "" when the offset is unlabeled
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
                    row.Scaled = (table[i] * info.Scale).ToString("F3", CultureInfo.InvariantCulture);
                }

                rows.Add(row);
            }
            return rows;
        }
    }
}
