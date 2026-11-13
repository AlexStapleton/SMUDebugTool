using ZenStates.Core;
using Xunit;
namespace ProfileCore.Tests
{
    public class BundledCoreCompatibilityTests
    {
        [Fact]
        public void Pinned_core_exposes_current_processor_operations()
        {
            Assert.NotNull(typeof(Cpu).GetMethod("TryWriteCurrentProcessorMsr", new[] { typeof(uint), typeof(ulong) }));
            Assert.NotNull(typeof(Cpu).GetMethod("TryReadCurrentProcessorMsr", new[] { typeof(uint), typeof(ulong).MakeByRefType() }));
        }
    }
}
