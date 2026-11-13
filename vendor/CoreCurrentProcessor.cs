using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.System32)]

namespace ZenStates.Core
{
    // Compiled alongside the pinned source; shares its existing driver instance.
    // The caller must pin a dedicated thread before calling these APIs.
    public partial class Cpu
    {
        public bool CanAccessCurrentProcessorMsr => _pawnAmd != null && _pawnAmd.IsLoaded;
        public bool TryReadCurrentProcessorMsr(uint address, out ulong value)
        {
            value = 0;
            return CanAccessCurrentProcessorMsr && _pawnAmd.ReadMsr(address, out value);
        }
        public bool TryWriteCurrentProcessorMsr(uint address, ulong value)
            => CanAccessCurrentProcessorMsr && _pawnAmd.WriteMsr(address, value);
    }
}
