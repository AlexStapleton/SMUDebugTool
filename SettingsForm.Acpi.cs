using System;
using System.Management;
using ZenStates.Core.Hardware;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private AcpiCommandControls acpiUi;
        private int acpiSelectionVersion;
        private void InitAcpiGuidance()
        {
            tabPageWmi.AutoScroll = true;
            acpiUi = new AcpiCommandControls(tableLayoutPanel13, comboBoxAvailableCommands, comboBoxAvailableValues, textBoxWmiArgument, buttonWmiCmdSend);
        }
        private static uint? AcpiNumber(ManagementBaseObject data, string field)
        {
            try { object value = data?.GetPropertyValue(field); return value == null ? null : (uint?)Convert.ToUInt32(value); }
            catch { return null; }
        }
        private ManagementBaseObject QueryAcpiMetadata(string method, uint id)
        {
            using (var input = classInstance.GetMethodParameters(method)) {
                if (input == null) return null;
                input["ID"] = id;
                return classInstance.InvokeMethod(method, input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) });
            }
        }
        private AcpiValueMetadata ReadAcpiMetadata(WmiCmdListItem command)
        {
            AcpiValueMetadata Range()
            {
                using (var result = QueryAcpiMetadata("GetMinMaxSteppingValues", command.value))
                using (var data = result?.GetPropertyValue("pack") as ManagementBaseObject) {
                    uint? id = AcpiNumber(data, "Value") ?? AcpiNumber(data, "ID"), min = AcpiNumber(data, "Min"), max = AcpiNumber(data, "Max"), step = AcpiNumber(data, "Stepping");
                    return id.HasValue && min.HasValue && max.HasValue && step.HasValue
                        ? AcpiValueMetadata.Range(command.value, id.Value, min.Value, max.Value, step.Value, AcpiNumber(data, "Current") ?? 0)
                        : AcpiValueMetadata.Unknown(command.value);
                }
            }
            AcpiValueMetadata Discrete()
            {
                using (var result = QueryAcpiMetadata("Getdvalues", command.value))
                using (var data = result?.GetPropertyValue("pack") as ManagementBaseObject)
                    return AcpiValueMetadata.Discrete(command.value, AcpiNumber(data, "ID") ?? 0, AcpiNumber(data, "Length") ?? 0,
                        data?.GetPropertyValue("DValuesBuffer") as uint[], AcpiNumber(data, "Current") ?? 0);
            }
            var metadata = AcpiValueMetadata.Unknown(command.value);
            try { metadata = command.PropertyFlag == 0 ? Discrete() : Range(); } catch { }
            if (!metadata.HasDomain) try { metadata = command.PropertyFlag == 0 ? Range() : Discrete(); } catch { }
            return metadata;
        }
    }
}
