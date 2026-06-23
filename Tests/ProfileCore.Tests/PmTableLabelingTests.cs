using System.Collections.Generic;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PmTableLabelingTests
    {
        [Fact]
        public void Label_applies_name_and_scale_for_known_offsets()
        {
            var table = new[] { 10.0f, 20.0f, 30.0f };
            var structure = new Dictionary<uint, SensorInfo>
            {
                { 0u, new SensorInfo("PPT", 1.0f) },
                { 8u, new SensorInfo("EDC", 0.5f) },
            };

            var rows = PmTableLabeling.Label(table, structure);

            Assert.Equal(3, rows.Count);
            Assert.Equal("PPT", rows[0].Name);
            Assert.Equal("10.000", rows[0].Scaled);
            Assert.Equal((uint)8, rows[2].Offset);
            Assert.Equal("EDC", rows[2].Name);
            Assert.Equal("15.000", rows[2].Scaled); // 30 * 0.5
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
