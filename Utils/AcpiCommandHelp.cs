using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZenStatesDebugTool
{
    public sealed class AcpiValueMetadata
    {
        public uint Id { get; private set; }
        public bool HasRange { get; private set; }
        public uint Min { get; private set; }
        public uint Max { get; private set; }
        public uint Step { get; private set; }
        public uint? Current { get; private set; }
        public IReadOnlyList<uint> Choices { get; private set; } = Array.AsReadOnly(new uint[0]);
        public string Note { get; private set; }
        public bool HasDomain => HasRange || Choices.Count > 0;
        public static AcpiValueMetadata Unknown(uint id, string note = "Firmware did not report allowed values.") => new AcpiValueMetadata { Id = id, Note = note };
        public static AcpiValueMetadata Range(uint id, uint returnedId, uint min, uint max, uint step, uint current)
        {
            if (returnedId != id || min > max || step == 0) return Unknown(id, "Firmware range metadata is missing or inconsistent.");
            var value = new AcpiValueMetadata { Id = id, HasRange = true, Min = min, Max = max, Step = step };
            // This WMI method uses zero when a current value is unavailable.
            if (current != 0 && value.Allows(current)) value.Current = current;
            return value;
        }
        public static AcpiValueMetadata Discrete(uint id, uint returnedId, uint length, uint[] buffer, uint current)
        {
            if (returnedId != id || length == 0 || length > 100 || buffer == null || length > buffer.Length) return Unknown(id);
            var value = new AcpiValueMetadata { Id = id, Choices = Array.AsReadOnly(buffer.Take((int)length).Distinct().ToArray()) };
            if (value.Choices.Contains(current)) value.Current = current;
            return value;
        }
        public bool Allows(uint value) => HasRange ? value >= Min && value <= Max && (value - Min) % Step == 0 : Choices.Count == 0 || Choices.Contains(value);
    }

    public sealed class AcpiCommandHelp
    {
        private static readonly Dictionary<string, string> Timings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["tcl"] = "CAS latency (tCL)", ["trcd"] = "Row-to-column delay (tRCD)", ["trcdrd"] = "Read row-to-column delay (tRCDRD)",
            ["trcdwr"] = "Write row-to-column delay (tRCDWR)", ["trp"] = "Row precharge delay (tRP)", ["tras"] = "Row active time (tRAS)",
            ["trc"] = "Row cycle time (tRC)", ["trfc"] = "Refresh recovery time (tRFC)", ["trfc2"] = "Refresh recovery time (tRFC2)",
            ["trfcsb"] = "Same-bank refresh recovery time (tRFCsb)", ["tfaw"] = "Four-activate window (tFAW)",
            ["trrds"] = "Short row activation delay (tRRDS)", ["trrdl"] = "Long row activation delay (tRRDL)",
            ["twr"] = "Write recovery time (tWR)", ["twtrs"] = "Short write-to-read delay (tWTRS)", ["twtrl"] = "Long write-to-read delay (tWTRL)",
            ["trtp"] = "Read-to-precharge delay (tRTP)", ["trdwr"] = "Read-to-write delay (tRDWR)", ["twrrd"] = "Write-to-read delay (tWRRD)",
            ["trdrdsc"] = "Same-chip-select read spacing (tRDRDSC)", ["trdrdscl"] = "Long read spacing (tRDRDSCL)",
            ["trdrdsd"] = "Same-DIMM read spacing (tRDRDSD)", ["trdrddd"] = "Different-DIMM read spacing (tRDRDDD)",
            ["twrwrsc"] = "Same-chip-select write spacing (tWRWRSC)", ["twrwrscl"] = "Long write spacing (tWRWRSCL)",
            ["twrwrsd"] = "Same-DIMM write spacing (tWRWRSD)", ["twrwrdd"] = "Different-DIMM write spacing (tWRWRDD)"
        };
        public uint Id { get; private set; }
        public string FirmwareName { get; private set; }
        public string Title { get; private set; }
        public string Unit { get; private set; } = "raw firmware units";
        public string Description { get; private set; }
        public string Example { get; private set; } = "Enter a decimal integer or 0x-prefixed hexadecimal value. Units are firmware-defined.";
        public bool IsSet { get; private set; }
        public decimal Scale { get; private set; } = 1;
        public string DisplayName => UiText.T(IsSet ? "Set" : "Read") + " " + UiText.T(Title);
        public static AcpiCommandHelp For(string name, uint id, bool isSet)
        {
            string clean = (name ?? "").Split(':')[0].Trim();
            string setting = Regex.Replace(clean, @"^(Set|Get)\s*", "", RegexOptions.IgnoreCase).Trim();
            string key = Regex.Replace(setting, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
            var info = new AcpiCommandHelp { Id = id, FirmwareName = clean, IsSet = isSet, Title = setting,
                Description = "This command is defined by your BIOS. Its encoding is not documented in this tool." };
            if (Timings.TryGetValue(key, out string timing)) {
                info.Title = timing; info.Unit = "memory clock cycles";
                info.Description = "Memory timing, expressed as a whole number of memory clock cycles. This is not nanoseconds or a voltage.";
                info.Example = "For example, enter 30 to request 30 cycles (CL30 for tCL). Use a value appropriate for your memory; 30 is an example, not a recommendation.";
            } else if (id == 0x20001) {
                info.Title = "Memory clock"; info.Unit = "MHz";
                info.Description = "Memory clock frequency. DDR transfer rate is twice this clock: 3000 MHz corresponds to 6000 MT/s.";
                info.Example = "Enter a clock in MHz, not the DDR transfer rate in MT/s.";
            } else if (id == 0x50001 || id == 0x50002 || id == 0x50003) {
                info.Title = id == 0x50001 ? "Package power limit (PPT)" : id == 0x50002 ? "Sustained current limit (TDC)" : "Peak current limit (EDC)";
                info.Unit = id == 0x50001 ? "W" : "A"; info.Scale = 1000;
                info.Description = "Enter the displayed unit. This interface encodes the value in thousandths; the tool converts it before sending.";
                info.Example = "Enter a value within the firmware input range shown below.";
            } else if (id == 0x20026) {
                info.Title = "Simultaneous multithreading (SMT)"; info.Unit = "0 = off, 1 = on";
                info.Description = "Enable or disable simultaneous multithreading through firmware.";
                info.Example = "Enter 0 for off or 1 for on. A restart may be required.";
            } else if (key.Contains("vdd") || key.Contains("volt")) {
                info.Title = setting;
                info.Description = "Voltage-related firmware setting. The argument encoding for this command is not verified; the value field is raw, not volts.";
                info.Example = "Use the documented BIOS encoding. Do not assume that 1.2 or 1200 represents 1.2 V for this command.";
            }
            return info;
        }
        public string Format(uint raw) => (raw / Scale).ToString("0.###", CultureInfo.InvariantCulture);
        public bool TryArgument(string text, AcpiValueMetadata metadata, out uint raw, out string error)
        {
            raw = 0; error = null; text = (text ?? "").Trim();
            bool parsed;
            if (Scale == 1 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                parsed = uint.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out raw);
            else {
                parsed = decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal number);
                if (!parsed && CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator != ".")
                    parsed = decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out number);
                if (parsed && number >= 0 && number <= uint.MaxValue / Scale && decimal.Truncate(number * Scale) == number * Scale) raw = (uint)(number * Scale);
                else parsed = false;
            }
            if (!parsed) error = "Enter a non-negative value in the displayed unit; it must encode as a 32-bit whole number.";
            else if (Id == 0x20026 && raw > 1) error = "SMT accepts 0 (off) or 1 (on).";
            else if (metadata == null || metadata.Id != Id) error = "The selected command changed; refresh its input metadata.";
            else if (!metadata.Allows(raw)) error = metadata.HasRange
                ? $"Firmware input range: {Format(metadata.Min)} to {Format(metadata.Max)}, step {Format(metadata.Step)} {Unit}."
                : "Choose one of the firmware-listed values.";
            return error == null;
        }
        public static uint? ResponseStatus(byte[] response) => response != null && response.Length >= 6
            ? (uint?)(response[2] | (uint)response[3] << 8 | (uint)response[4] << 16 | (uint)response[5] << 24) : null;
        public string ResponseText(byte[] response, uint argument)
        {
            uint? status = ResponseStatus(response);
            if (!status.HasValue) return "No complete firmware status was returned; the command outcome is unknown.";
            if (status != 0) return $"Firmware rejected the command (status 0x{status:X8}).";
            return IsSet ? $"Firmware accepted {UiText.T(Title)} = {Format(argument)} {UiText.T(Unit)}. Active hardware value is not verified; a restart may be required."
                : "Firmware returned a response. Raw data is available in Activity log; use the firmware-value field for decoded metadata.";
        }
    }
}
