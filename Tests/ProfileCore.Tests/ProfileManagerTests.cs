using System;
using System.IO;
using System.Linq;
using Xunit;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class ProfileManagerTests : IDisposable
    {
        private readonly string _dir;
        public ProfileManagerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "smu_profiles_" + Guid.NewGuid().ToString("N"));
        }
        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Fact]
        public void Save_then_Load_returns_equivalent_profile()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Quiet", Fmax = 4800m, PptWatts = 88 });

            var loaded = mgr.Load("Quiet");

            Assert.Equal("Quiet", loaded.Name);
            Assert.Equal(4800m, loaded.Fmax);
            Assert.Equal(88, loaded.PptWatts);
        }

        [Fact]
        public void List_returns_saved_names_sorted()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Zulu" });
            mgr.Save(new Profile { Name = "Alpha" });

            Assert.Equal(new[] { "Alpha", "Zulu" }, mgr.List().ToArray());
        }

        [Fact]
        public void Delete_removes_profile()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Temp" });
            mgr.Delete("Temp");
            Assert.Empty(mgr.List());
            Assert.Null(mgr.Load("Temp"));
        }

        [Fact]
        public void Load_missing_returns_null()
        {
            var mgr = new ProfileManager(_dir);
            Assert.Null(mgr.Load("Nope"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("bad/name")]
        [InlineData("bad:name")]
        public void IsValidName_rejects_invalid(string name)
        {
            Assert.False(ProfileManager.IsValidName(name));
        }

        [Fact]
        public void Save_invalid_name_throws()
        {
            var mgr = new ProfileManager(_dir);
            Assert.Throws<ArgumentException>(() => mgr.Save(new Profile { Name = "a/b" }));
        }

        [Fact]
        public void ParseLegacy_reads_margins_and_fmax()
        {
            var lines = new[] { "[0,-25]", "[3,-10]", "fmax=5050" };
            var profile = ProfileManager.ParseLegacy(lines);

            Assert.Equal("Default", profile.Name);
            Assert.Equal(-25, profile.CoMargins[0]);
            Assert.Equal(-10, profile.CoMargins[3]);
            Assert.Equal(5050m, profile.Fmax);
        }

        [Fact]
        public void MigrateLegacyIfNeeded_creates_Default_from_legacy_file()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(Path.Combine(_dir, ProfileManager.LegacyFileName),
                new[] { "[0,-20]", "fmax=4900" });
            var mgr = new ProfileManager(_dir);

            bool migrated = mgr.MigrateLegacyIfNeeded();

            Assert.True(migrated);
            var loaded = mgr.Load("Default");
            Assert.Equal(-20, loaded.CoMargins[0]);
            Assert.Equal(4900m, loaded.Fmax);
        }

        [Fact]
        public void MigrateLegacyIfNeeded_is_noop_when_profiles_exist()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Existing" });
            File.WriteAllLines(Path.Combine(_dir, ProfileManager.LegacyFileName), new[] { "[0,-20]" });

            Assert.False(mgr.MigrateLegacyIfNeeded());
        }
    }
}
