using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    public static class RegisterCatalog
    {
        // Keyed by (Kind, Address/Leaf). Populated in later tasks.
        private static readonly Dictionary<(RegisterKind, uint), RegisterDefinition> Map =
            new Dictionary<(RegisterKind, uint), RegisterDefinition>();

        public static bool TryGet(RegisterKind kind, uint address, out RegisterDefinition def)
            => Map.TryGetValue((kind, address), out def);

        // Used by the population helpers in later tasks.
        internal static void Add(RegisterDefinition def) => Map[(def.Kind, def.Address)] = def;
    }
}
