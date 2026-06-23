using System.Collections.Generic;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class SmuCommandNamesTests
    {
        private static KeyValuePair<string, uint> Kv(string k, uint v)
            => new KeyValuePair<string, uint>(k, v);

        [Fact]
        public void Build_maps_values_to_names_and_skips_zero()
        {
            var map = SmuCommandNames.Build(new[]
            {
                Kv("SetPBOScalar", 0x26),
                Kv("GetTableVersion", 0x05),
                Kv("Unsupported", 0x00),
            });

            Assert.Equal("SetPBOScalar", SmuCommandNames.Resolve(map, 0x26));
            Assert.Equal("GetTableVersion", SmuCommandNames.Resolve(map, 0x05));
            Assert.False(map.ContainsKey(0x00));
        }

        [Fact]
        public void Build_joins_names_sharing_a_value()
        {
            var map = SmuCommandNames.Build(new[]
            {
                Kv("SetMaxCpuFreq", 0x10),
                Kv("AltName", 0x10),
            });
            Assert.Equal("SetMaxCpuFreq/AltName", SmuCommandNames.Resolve(map, 0x10));
        }

        [Fact]
        public void Resolve_returns_null_for_unknown()
        {
            var map = SmuCommandNames.Build(new[] { Kv("X", 0x01) });
            Assert.Null(SmuCommandNames.Resolve(map, 0x99));
        }
    }
}
