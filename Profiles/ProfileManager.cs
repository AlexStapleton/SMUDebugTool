using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZenStatesDebugTool.Profiles
{
    public class ProfileManager
    {
        public const string LegacyFileName = "co_profile.txt";
        private readonly string _dir;

        public ProfileManager(string profilesDirectory)
        {
            _dir = profilesDirectory ?? throw new ArgumentNullException(nameof(profilesDirectory));
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        public void EnsureDirectory()
        {
            if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
        }

        private string PathFor(string name) => Path.Combine(_dir, name + ".json");

        public IEnumerable<string> List()
        {
            if (!Directory.Exists(_dir)) return new List<string>();
            return Directory.GetFiles(_dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Profile Load(string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("Invalid profile name.", nameof(name));
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            var profile = JsonConvert.DeserializeObject<Profile>(File.ReadAllText(path));
            if (profile != null) profile.Name = name; // filename is the source of truth
            return profile;
        }

        public void Save(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!IsValidName(profile.Name)) throw new ArgumentException("Invalid profile name.");
            EnsureDirectory();
            File.WriteAllText(PathFor(profile.Name),
                JsonConvert.SerializeObject(profile, Formatting.Indented));
        }

        public void Delete(string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("Invalid profile name.");
            var path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
        }

        // --- Legacy migration (Task 3 adds tests for these) ---

        public bool MigrateLegacyIfNeeded()
        {
            EnsureDirectory();
            if (List().Any()) return false;
            var legacy = Path.Combine(_dir, LegacyFileName);
            if (!File.Exists(legacy)) return false;
            var profile = ParseLegacy(File.ReadAllLines(legacy));
            Save(profile);
            return true;
        }

        public static Profile ParseLegacy(IEnumerable<string> lines)
        {
            var profile = new Profile { Name = "Default" };
            foreach (var line in lines)
            {
                if (line.StartsWith("["))
                {
                    var values = line.Replace("[", "").Replace("]", "").Replace(" ", "").Split(',');
                    if (values.Length == 2
                        && int.TryParse(values[0], out int index)
                        && int.TryParse(values[1], out int margin))
                    {
                        profile.CoMargins[index] = margin;
                    }
                }
                else if (line.StartsWith("fmax="))
                {
                    if (decimal.TryParse(line.Substring(5), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out decimal fmax))
                    {
                        profile.Fmax = fmax;
                    }
                }
            }
            return profile;
        }
    }
}
