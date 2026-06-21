using System.Collections.Generic;
using Newtonsoft.Json;
using Xunit;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class ProfileTests
    {
        [Fact]
        public void Profile_round_trips_through_json()
        {
            var profile = new Profile
            {
                Name = "Gaming",
                CoMargins = new Dictionary<int, int> { { 0, -20 }, { 5, -15 } },
                CurveShaperTiers = new List<CurveShaperTier>
                {
                    new CurveShaperTier { Low = 1, Medium = 2, High = 3 }
                },
                Fmax = 5200m,
                PptWatts = 142,
                TdcAmps = 95,
                EdcAmps = 140,
                PboScalar = 3
            };

            var json = JsonConvert.SerializeObject(profile);
            var restored = JsonConvert.DeserializeObject<Profile>(json);

            Assert.Equal("Gaming", restored.Name);
            Assert.Equal(-20, restored.CoMargins[0]);
            Assert.Equal(-15, restored.CoMargins[5]);
            Assert.Equal(2, restored.CurveShaperTiers[0].Medium);
            Assert.Equal(5200m, restored.Fmax);
            Assert.Equal(142, restored.PptWatts);
            Assert.Equal(95, restored.TdcAmps);
            Assert.Equal(140, restored.EdcAmps);
            Assert.Equal(3, restored.PboScalar);
        }
    }
}
