using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class TabGuide
    {
        public string Summary { get; }
        public string Instructions { get; }
        public TabGuide(string summary, string instructions) { Summary = summary; Instructions = instructions; }
    }
    public static class TabGuidance
    {
        private static readonly Dictionary<string, TabGuide> Guides = new Dictionary<string, TabGuide> {
            ["tabPageCPU"] = new TabGuide("Choose enabled cores, SMT and thermal protection.",
                "Core control changes which physical cores are enabled. Checked cores remain enabled; unchecked cores are requested disabled. Apply can require a restart.\r\n\r\nSMT controls simultaneous multithreading. PROCHOT is thermal throttling: checked means protection is enabled. These controls are separate from CO offsets and frequency tuning."),
            ["tabPageSmu"] = new TabGuide("Raw SMU commands: IDs and comma-separated arguments are hexadecimal.",
                "For ordinary power limits, CO and boost limits, use PBO and the profile controls.\r\n\r\nThis page sends raw mailbox packets. Pick a mailbox, enter a hexadecimal command ID, then up to six comma-separated hexadecimal arguments, for example 0x1,0x0. An unprefixed 10 also means hexadecimal 0x10 here. Command meaning, argument encoding and returned values depend on the CPU and mailbox. A resolved command name is shown when known; an unknown command is not assigned guessed units. CMD/RSP/ARG addresses are mailbox register addresses, not tuning values.\r\n\r\nSend executes once. Monitor and PMTable show diagnostic data. Raw responses are available in History > Activity log."),
            ["tabPagePci"] = new TabGuide("Read or write a 32-bit PCI/SMN register using hexadecimal addresses and values.",
                "Enter a register address in hex, for example 0x50000, and click Read. The value field is a raw 32-bit word, not a voltage or frequency. Recognized addresses are named and decoded; other addresses remain raw. Write sends the entire entered word. Use a structured tuning tab when one is available.\r\n\r\nScan/Monitor take start and end register addresses. Memory Dump takes byte addresses and an output filename; these are a separate address space. Results and decoded diagnostics are available in History > Activity log."),
            ["tabPageMsr"] = new TabGuide("Raw MSR access: address and EAX/EDX fields are hexadecimal.",
                "MSRs are CPU registers. Enter an address, for example 0xC0010064, and click Read. EAX is the low 32 bits and EDX is the high 32 bits of the 64-bit value. Write replaces the raw value; it does not adjust only one decoded field.\r\n\r\nUse PStates for supported P-state frequency and VID edits. That editor preserves unrelated fields. Recognized raw registers show their names; unknown addresses need documentation for your CPU. Scan takes a start and end MSR address."),
            ["tabPageCPUID"] = new TabGuide("Read CPU identification leaves; all register numbers are hexadecimal.",
                "CPUID is an identification query, not a tuning write. Enter a leaf such as 0x00000001, then Read to obtain EAX, EBX, ECX and EDX. Scan enumerates supported leaves. Decode CPU signature interprets an EAX signature such as 0x00A60F12; it is not a register address. Decoded information is available in History > Activity log."),
            ["tabPagePbo"] = new TabGuide("CO uses signed offset steps; the boost limit is in MHz. Apply sends once.",
                "Each CO box requests an offset for a validated physical core. Negative steps shift its curve down; steps are not millivolts. The + and - controls edit values locally. Apply sends the requested offsets. Re-read from CPU refreshes observations while preserving pending drafts.\r\n\r\nThe boost limit is an absolute requested limit in MHz, not a +MHz offset. Profile-side PPT is watts; TDC/EDC are amps; Scalar is a multiplier. Zero power limits mean leave unchanged. Apply changes sends edited fields; Apply all settings sends every included field. Preview changes is optional. Yellow fields are local edits, and Discard edits sends no commands. Sent without readback is not the same as a confirmed hardware value."),
            ["tabPageCS"] = new TabGuide("Curve Shaper offsets use steps, grouped by frequency tier and temperature band.",
                "Rows are frequency tiers (minimum through maximum). Columns are low, medium and high temperature bands. Values are signed curve steps, not MHz, degrees or millivolts. Editing a band changes that tier's three-band request as one command. Apply sends the requested tiers once.\r\n\r\nRefresh preserves pending edits. Firmware may not expose reliable readback, so the tool distinguishes session values from CPU observations and does not label an accepted-only write as confirmed."),
            ["tabPageWmi"] = new TabGuide("Firmware settings: choose a command to see its units, input limits and last reported value.",
                "The page reads both firmware value lists and min/max/step metadata. A range is not a list: the choices box is hidden when you should type a value. Zero is retained when firmware lists it as a valid choice.\r\n\r\ntCL means CAS latency in memory clock cycles: entering 30 requests 30 cycles, not 30 ns. This is a format example, not a recommended timing. Use your memory/BIOS specifications and the displayed firmware input range. Known PPT/TDC/EDC commands are displayed in W/A and converted to firmware units. Unknown voltage encodings remain explicitly raw.\r\n\r\nFirmware values are snapshots and may be setup values. Acceptance does not prove live application; memory settings commonly need a restart. If firmware supplies no limits, the page says so and accepts a documented manual argument. Raw status and packets remain in History > Activity log."),
            ["tabPagePstates"] = new TabGuide("Read a P-state before editing. Frequency/VID and base clock are separate rows.",
                "Choose P0-P7 and Read. Disabled or unknown layouts are read-only. Frequency ID (FID) and Divider (DID) are raw register fields; the derived frequency shows their meaning in MHz. On Zen 5, MHz = FID x 5; FID 860 represents 4300 MHz. Earlier supported layouts use MHz = FID x 200 / DID. DID is not used on Zen 5.\r\n\r\nRequested VID is an encoded voltage request. The derived voltage is not measured Vcore. Frequency and VID have separate Write actions. Supported writes use fresh per-processor reads and preserve unrelated fields.\r\n\r\nThe Base clock section shows the observed BCLK and a requested MHz value. Apply BCLK is separate from P-state frequency and VID writes."),
            ["tabPageInfo"] = new TabGuide("CPU, motherboard, firmware and diagnostic report information.",
                "This page identifies the CPU, motherboard, BIOS and SMU firmware. Debug Report exports diagnostic information for troubleshooting; it is separate from named tuning profiles. Profile files store requested tuning values, not this report."),
            ["tabPageFreqOC"] = new TabGuide("Manual OC uses MHz and volts, subject to CPU/firmware support.",
                "Frequency fields are MHz for validated physical core targets. The voltage field is a requested value in volts and uses the supported VID encoding; it is not measured Vcore. Applying manual values enables OC mode. Disable OC Mode returns control to normal boosting when the firmware accepts that command.\r\n\r\nSome CPUs expose these controls but reject writes. Locked/unsupported layouts are identified by the application. A command acceptance alone is not a verified frequency or measured voltage.")
        };
        public static TabGuide For(string name) => Guides.TryGetValue(name, out var guide) ? guide : new TabGuide("Hardware controls and diagnostics.", "Input formats and meanings depend on the selected control.");
        public static Label Attach(TabPage tab)
        {
            var guide = For(tab.Name);
            var children = tab.Controls.Cast<Control>().ToArray();
            var content = new Panel { Name = "guidedContent", Dock = DockStyle.Fill, AutoScroll = true, Size = tab.ClientSize };
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(3) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new Label { Text = guide.Summary, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3) };
            var help = new LinkLabel { Text = "Help", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(6, 3, 3, 3) };
            help.LinkClicked += (s, e) => {
                var form = new Form { Text = tab.Text + " — " + UiText.T("Help"), Width = 760, Height = 480 };
                UiLayout.Configure(form, new Size(450, 300));
                form.Controls.Add(new TextBox { Text = guide.Instructions, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, WordWrap = true });
                form.Show(tab.FindForm());
            };
            tab.SuspendLayout();
            foreach (var child in children) content.Controls.Add(child);
            header.Controls.Add(text, 0, 0); header.Controls.Add(help, 1, 0);
            tab.Controls.Add(content); tab.Controls.Add(header);
            void Wrap() => text.MaximumSize = new Size(Math.Max(100, tab.ClientSize.Width - help.PreferredWidth - 28), 0);
            tab.SizeChanged += (s, e) => Wrap(); Wrap(); tab.ResumeLayout(true);
            return text;
        }
    }
}
