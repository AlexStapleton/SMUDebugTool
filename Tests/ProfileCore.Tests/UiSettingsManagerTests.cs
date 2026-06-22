using System;
using System.IO;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class UiSettingsManagerTests : IDisposable
    {
        private readonly string _path;

        public UiSettingsManagerTests()
        {
            _path = Path.Combine(Path.GetTempPath(), "smu_uisettings_" + Guid.NewGuid().ToString("N") + ".json");
        }

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void Load_missing_file_returns_defaults()
        {
            var mgr = new UiSettingsManager(_path);

            var settings = mgr.Load();

            Assert.NotNull(settings);
            Assert.Empty(settings.HiddenTabs);
            Assert.Null(settings.DefaultTabKey);
        }

        [Fact]
        public void Save_then_Load_round_trips_hidden_and_default()
        {
            var mgr = new UiSettingsManager(_path);
            var settings = new UiSettings { DefaultTabKey = "tabPagePbo" };
            settings.HiddenTabs.Add("tabPageCPUID");
            settings.HiddenTabs.Add("tabPagePci");
            mgr.Save(settings);

            var loaded = mgr.Load();

            Assert.Equal("tabPagePbo", loaded.DefaultTabKey);
            Assert.Contains("tabPageCPUID", loaded.HiddenTabs);
            Assert.Contains("tabPagePci", loaded.HiddenTabs);
            Assert.Equal(2, loaded.HiddenTabs.Count);
        }

        [Fact]
        public void Load_corrupt_file_returns_defaults()
        {
            File.WriteAllText(_path, "{ this is not valid json ]");
            var mgr = new UiSettingsManager(_path);

            var settings = mgr.Load();

            Assert.NotNull(settings);
            Assert.Empty(settings.HiddenTabs);
            Assert.Null(settings.DefaultTabKey);
        }

        [Fact]
        public void Load_json_with_null_hidden_tabs_yields_empty_set()
        {
            File.WriteAllText(_path, "{ \"HiddenTabs\": null, \"DefaultTabKey\": \"tabPageInfo\" }");
            var mgr = new UiSettingsManager(_path);

            var settings = mgr.Load();

            Assert.NotNull(settings.HiddenTabs);
            Assert.Empty(settings.HiddenTabs);
            Assert.Equal("tabPageInfo", settings.DefaultTabKey);
        }
    }
}
