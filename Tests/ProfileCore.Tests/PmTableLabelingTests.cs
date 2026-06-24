using System.Collections.Generic;
using System.Linq;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PmTableLabelingTests
    {
        [Fact]
        public void BuildNamedOffsetMap_converts_byte_offset_to_element_index_and_skips_negative()
        {
            var map = PmTableLabeling.BuildNamedOffsetMap(new[]
            {
                new KeyValuePair<string, int>("FCLK", 0x11C),       // 284 / 4 -> index 71
                new KeyValuePair<string, int>("Cores Power", -1),   // unavailable -> skipped
            });

            Assert.Single(map);
            Assert.True(map.ContainsKey(71));
            Assert.Equal("FCLK", map[71].Name);
            Assert.Equal(1.0f, map[71].Scale);
            Assert.DoesNotContain(map.Values, v => v.Name == "Cores Power");
        }

        [Fact]
        public void BuildNamedOffsetMap_first_label_wins_on_duplicate_index()
        {
            var map = PmTableLabeling.BuildNamedOffsetMap(new[]
            {
                new KeyValuePair<string, int>("A", 8),
                new KeyValuePair<string, int>("B", 8),
            });
            Assert.Equal("A", map[2].Name);
        }

        [Fact]
        public void Label_applies_name_and_scale_by_element_index()
        {
            // ZenStates' GetPmTableStructure keys sensors by float-array ELEMENT INDEX
            // (e.g. 7, 11, 66), not byte offset. The Offset field is byte offset (index*4)
            // for display only; the lookup must use the element index.
            var table = new[] { 10.0f, 20.0f, 30.0f };
            var structure = new Dictionary<uint, SensorInfo>
            {
                { 0u, new SensorInfo("PPT", 1.0f) },
                { 2u, new SensorInfo("EDC", 0.5f) },
            };

            var rows = PmTableLabeling.Label(table, structure);

            Assert.Equal(3, rows.Count);
            Assert.Equal("PPT", rows[0].Name);
            Assert.Equal("10.000", rows[0].Scaled);
            // row index 2 matched by key 2; its displayed Offset is the byte offset 8.
            Assert.Equal(2, rows[2].Index);
            Assert.Equal((uint)8, rows[2].Offset);
            Assert.Equal("EDC", rows[2].Name);
            Assert.Equal("15.000", rows[2].Scaled); // 30 * 0.5
            // byte-offset key (8) must NOT match anything in a 3-element table.
            Assert.Equal("", rows[1].Name);
        }

        [Fact]
        public void Label_leaves_unknown_offsets_blank()
        {
            var rows = PmTableLabeling.Label(new[] { 1.0f, 2.0f }, null);
            Assert.All(rows, r => Assert.Equal("", r.Name));
            Assert.All(rows, r => Assert.Equal("", r.Scaled));
            Assert.Equal((uint)4, rows[1].Offset);
        }

        [Fact]
        public void Label_returns_empty_for_null_table()
        {
            Assert.Empty(PmTableLabeling.Label(null, null));
        }
    }
}
