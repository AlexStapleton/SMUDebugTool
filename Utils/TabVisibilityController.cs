using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    // Owns the canonical tab list and drives the View menu: which tabs are visible and which
    // one is selected on startup. WinForms can't hide a TabPage in place, so visibility is
    // applied by rebuilding the TabControl's pages from the canonical order. The pure
    // ordering/default logic lives in TabLayout so it can be unit tested.
    public class TabVisibilityController
    {
        private class TabEntry
        {
            public string Key;
            public TabPage Page;
            public string Title;
            public ToolStripMenuItem VisibilityItem;
            public ToolStripMenuItem DefaultItem;
        }

        private readonly TabControl _tabControl;
        private readonly UiSettingsManager _settingsManager;
        private readonly List<TabEntry> _entries = new List<TabEntry>();
        private readonly UiSettings _settings;

        // Optional status callback so rejected actions can surface a hint.
        public Action<string> OnStatus;

        public TabVisibilityController(TabControl tabControl, UiSettingsManager settingsManager)
        {
            _tabControl = tabControl ?? throw new ArgumentNullException(nameof(tabControl));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _settings = settingsManager.Load();
            if (_settings.HiddenTabs == null) _settings.HiddenTabs = new HashSet<string>();
        }

        // Captures the current TabControl pages (and their order) as the canonical list.
        // Call after every tab — including dynamically built ones — has been added.
        public void RegisterTabsFromControl()
        {
            _entries.Clear();
            foreach (TabPage page in _tabControl.TabPages)
                _entries.Add(new TabEntry { Key = page.Name, Page = page, Title = page.Text });
        }

        // Populates the given View menu with a checkable item per tab plus a "Default Tab"
        // submenu of radio-style items.
        public void BuildViewMenu(ToolStripMenuItem viewMenu)
        {
            var defaultSubmenu = new ToolStripMenuItem("Default Tab");
            foreach (var entry in _entries)
            {
                var captured = entry;

                var visItem = new ToolStripMenuItem(entry.Title);
                visItem.Click += (s, e) => OnToggleVisibility(captured);
                entry.VisibilityItem = visItem;
                viewMenu.DropDownItems.Add(visItem);

                var defItem = new ToolStripMenuItem(entry.Title);
                defItem.Click += (s, e) => OnSelectDefault(captured);
                entry.DefaultItem = defItem;
                defaultSubmenu.DropDownItems.Add(defItem);
            }

            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(defaultSubmenu);
        }

        // Applies the persisted visibility and selects the startup tab. Call after BuildViewMenu.
        public void ApplyInitial()
        {
            ApplyVisibility();
            RefreshMenuChecks();
            string startup = TabLayout.ResolveStartupTabKey(
                _entries.Select(x => x.Key), _settings.HiddenTabs, _settings.DefaultTabKey);
            SelectByKey(startup);
        }

        private void OnToggleVisibility(TabEntry entry)
        {
            bool currentlyHidden = _settings.HiddenTabs.Contains(entry.Key);
            if (!currentlyHidden)
            {
                // Hiding: refuse if this is the last visible tab.
                int visibleCount = _entries.Count(x => !_settings.HiddenTabs.Contains(x.Key));
                if (visibleCount <= 1)
                {
                    RefreshMenuChecks(); // undo the auto-toggle on the menu item
                    OnStatus?.Invoke("At least one tab must stay visible.");
                    return;
                }
                _settings.HiddenTabs.Add(entry.Key);
            }
            else
            {
                _settings.HiddenTabs.Remove(entry.Key);
            }

            ApplyVisibility();
            RefreshMenuChecks();
            Save();
        }

        private void OnSelectDefault(TabEntry entry)
        {
            _settings.DefaultTabKey = entry.Key;
            RefreshMenuChecks();
            Save();
        }

        private void ApplyVisibility()
        {
            var visibleKeys = TabLayout.OrderedVisibleKeys(_entries.Select(x => x.Key), _settings.HiddenTabs);
            _tabControl.SuspendLayout();
            _tabControl.TabPages.Clear();
            foreach (var key in visibleKeys)
            {
                var entry = _entries.FirstOrDefault(x => x.Key == key);
                if (entry != null)
                    _tabControl.TabPages.Add(entry.Page);
            }
            _tabControl.ResumeLayout();
        }

        private void RefreshMenuChecks()
        {
            foreach (var entry in _entries)
            {
                if (entry.VisibilityItem != null)
                    entry.VisibilityItem.Checked = !_settings.HiddenTabs.Contains(entry.Key);
                if (entry.DefaultItem != null)
                    entry.DefaultItem.Checked = entry.Key == _settings.DefaultTabKey;
            }
        }

        private void SelectByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var entry = _entries.FirstOrDefault(x => x.Key == key);
            if (entry != null && _tabControl.TabPages.Contains(entry.Page))
                _tabControl.SelectedTab = entry.Page;
        }

        private void Save()
        {
            try { _settingsManager.Save(_settings); }
            catch { /* a persistence failure shouldn't break the UI */ }
        }
    }
}
