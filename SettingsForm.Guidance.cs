using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private void InitTabGuidance()
        {
            labelCMD.Text = "Command ID (hex)"; labelARG0.Text = "Arguments (hex, comma-separated)";
            label5.Text = "Register address (hex)"; label7.Text = "32-bit value (hex)";
            label15.Text = "MSR address (hex)"; label17.Text = "High 32 bits (EDX, hex)"; label20.Text = "Low 32 bits (EAX, hex)";
            label21.Text = "CPUID leaf (hex)"; label50.Text = "CPU signature (hex)";
            label51.Text = "Boost limit (MHz)";
            label10.Text = "Frequency ID (FID)"; label11.Text = "Frequency (derived)";
            foreach (var tab in tabControl1.TabPages.Cast<TabPage>())
            {
                var summary = TabGuidance.Attach(tab);
                if (tab == tabPageMsr || tab == tabPagePci) {
                    var address = tab == tabPageMsr ? textBoxMsrAddress : textBoxPciAddress;
                    var kind = tab == tabPageMsr ? RegisterKind.Msr : RegisterKind.Pci;
                    void Update() {
                        string value = address.Text.Trim(); if (value.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
                        summary.Text = TabGuidance.For(tab.Name).Summary + (uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint number) && RegisterCatalog.TryGet(kind, number, decodeContext, out var definition)
                            ? "\r\n" + definition.Name + ": " + definition.Description : "");
                    }
                    address.TextChanged += (s, e) => Update(); Update();
                }
                if (tab == tabPageSmu) {
                    void Update() {
                        string value = textBoxCMD.Text.Trim(); if (value.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
                        string name = uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint number) ? SmuCommandNames.Resolve(smuNameMap, number) : null;
                        summary.Text = TabGuidance.For(tab.Name).Summary + (name == null ? "" : "\r\n" + name);
                    }
                    textBoxCMD.TextChanged += (s, e) => Update(); comboBoxMailboxSelect.SelectedIndexChanged += (s, e) => Update(); Update();
                }
            }
        }
    }
}
