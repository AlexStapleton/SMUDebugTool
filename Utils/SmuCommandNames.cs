using System.Collections.Generic;
using System.Linq;

namespace ZenStatesDebugTool
{
    // Pure mapping from SMU command IDs to their names. The IDs are read from the
    // live mailbox by the main-project adapter; this part is hardware-free so it
    // can be unit-tested.
    public static class SmuCommandNames
    {
        // Builds value -> name. Zero-valued (unsupported/unset) messages are
        // skipped; multiple names sharing a value are joined with "/".
        public static IReadOnlyDictionary<uint, string> Build(IEnumerable<KeyValuePair<string, uint>> messages)
        {
            var map = new Dictionary<uint, string>();
            if (messages == null) return map;

            foreach (var m in messages)
            {
                if (m.Value == 0) continue;
                if (map.TryGetValue(m.Value, out string existing))
                {
                    if (!existing.Split('/').Contains(m.Key))
                        map[m.Value] = existing + "/" + m.Key;
                }
                else
                {
                    map[m.Value] = m.Key;
                }
            }
            return map;
        }

        // Returns the resolved name, or null when not found.
        public static string Resolve(IReadOnlyDictionary<uint, string> map, uint value)
            => map != null && map.TryGetValue(value, out string name) ? name : null;
    }
}
