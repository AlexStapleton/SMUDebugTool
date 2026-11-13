using System.Reflection;
using ZenStates.Core;
using Xunit;

namespace ProfileCore.Tests
{
    public class BundledCoreCompatibilityTests
    {
        [Fact]
        public void Bundled_core_exposes_the_expected_per_processor_driver()
        {
            // Metadata only: never construct Cpu/AmdFamily17 or initialize a driver.
            var field = typeof(Cpu).GetField("_pawnAmd", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            Assert.Equal(typeof(AmdFamily17), field.FieldType);
            Assert.NotNull(typeof(AmdFamily17).GetMethod("WriteMsr", new[] { typeof(uint), typeof(ulong) }));
            Assert.NotNull(typeof(AmdFamily17).GetMethod("ReadMsr", new[] { typeof(uint), typeof(ulong).MakeByRefType() }));
        }
    }
}
