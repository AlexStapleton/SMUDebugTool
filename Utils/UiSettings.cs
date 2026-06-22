using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    // Persisted UI preferences (see UiSettingsManager). Plain POCO so it serializes cleanly.
    public class UiSettings
    {
        // Keys (TabPage.Name) of tabs the user has hidden. Storing *hidden* rather than
        // *visible* means tabs added in future versions default to visible.
        public HashSet<string> HiddenTabs { get; set; } = new HashSet<string>();

        // Key (TabPage.Name) of the tab to select on startup, or null/empty for "first visible".
        public string DefaultTabKey { get; set; }
    }
}
