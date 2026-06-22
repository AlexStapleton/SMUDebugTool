using System;
using System.IO;
using Newtonsoft.Json;

namespace ZenStatesDebugTool
{
    // Loads/saves UiSettings as JSON (default settings.json next to the exe).
    public class UiSettingsManager
    {
        private readonly string _path;

        public UiSettingsManager(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        // Never throws on read: a missing or unparseable file yields a default instance, so a
        // corrupt settings file can't stop the app from starting.
        public UiSettings Load()
        {
            try
            {
                if (!File.Exists(_path)) return new UiSettings();
                var settings = JsonConvert.DeserializeObject<UiSettings>(File.ReadAllText(_path));
                if (settings == null) return new UiSettings();
                if (settings.HiddenTabs == null) settings.HiddenTabs = new System.Collections.Generic.HashSet<string>();
                return settings;
            }
            catch
            {
                return new UiSettings();
            }
        }

        public void Save(UiSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            File.WriteAllText(_path, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
    }
}
