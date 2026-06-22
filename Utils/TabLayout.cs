using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    // Pure tab-visibility resolution, kept WinForms-free so it can be unit tested. The
    // canonical key list defines the fixed display order; hidden keys are removed from it.
    public static class TabLayout
    {
        // Visible keys in canonical order, excluding any hidden ones.
        public static List<string> OrderedVisibleKeys(IEnumerable<string> canonical, ICollection<string> hidden)
        {
            var result = new List<string>();
            foreach (var key in canonical)
            {
                if (hidden == null || !hidden.Contains(key))
                    result.Add(key);
            }
            return result;
        }

        // The tab to select on startup: the stored default if it's currently visible;
        // otherwise the first visible tab; otherwise null (nothing visible).
        public static string ResolveStartupTabKey(IEnumerable<string> canonical, ICollection<string> hidden, string storedDefault)
        {
            var visible = OrderedVisibleKeys(canonical, hidden);
            if (!string.IsNullOrEmpty(storedDefault) && visible.Contains(storedDefault))
                return storedDefault;
            return visible.Count > 0 ? visible[0] : null;
        }
    }
}
