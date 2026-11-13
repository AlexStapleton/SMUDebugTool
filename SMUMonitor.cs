using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZenStates.Core;
using ZenStates.Core.Hardware;
using ZenStates.Core.Hardware.Smu;

namespace ZenStatesDebugTool
{
    public partial class SMUMonitor : Form
    {
        private readonly Cpu CPU;
        private readonly IReadOnlyDictionary<uint, string> nameMap;
        private readonly Timer timer = new Timer { Interval = 15 };
        private bool refreshing;
        private bool running;
        private int generation;
        private bool closing;
        private readonly BindingList<SmuMonitorItem> list = new BindingList<SmuMonitorItem>();
        private uint prevCmdValue, prevArgValue;
        private const int MaxRows = 2000;
        private readonly uint SMU_ADDR_MSG, SMU_ADDR_ARG, SMU_ADDR_RSP;
        private class SmuMonitorItem
        {
            public string Cmd { get; set; }
            public string Arg { get; set; }
            public string Rsp { get; set; }
        }
        public SMUMonitor(Cpu cpu, uint addrMsg, uint addrArg, uint addrRsp)
        {
            CPU = cpu;
            // Metadata only: no driver I/O or lock acquisition on the message loop.
            nameMap = SmuCommandNames.Build(SmuDecodeAdapter.ReadMessages(cpu.smu));
            SMU_ADDR_MSG = addrMsg; SMU_ADDR_ARG = addrArg; SMU_ADDR_RSP = addrRsp;
            InitializeComponent();
            UiLayout.Configure(this, new System.Drawing.Size(500, 300));
            labelCmdAddr.Text = $"0x{addrMsg:X8}";
            labelRspAddr.Text = $"0x{addrRsp:X8}";
            labelArgAddr.Text = $"0x{addrArg:X8}";
            dataGridView2.DataSource = list;
            timer.Tick += PollOnce;
        }
        private async void PollOnce(object sender, EventArgs e)
        {
            if (refreshing || closing || !running || Hardware.IsClosing) return;
            refreshing = true;
            int run = generation;
            try
            {
                var snapshot = await Task.Run(() =>
                {
                    uint[] values;
                    return Hardware.TryRead(() => new[] { CPU.ReadDword(SMU_ADDR_MSG), CPU.ReadDword(SMU_ADDR_ARG), CPU.ReadDword(SMU_ADDR_RSP) }, out values) ? values : null;
                });
                if (snapshot == null || closing || !running || generation != run || IsDisposed) return;
                uint msg = snapshot[0], arg = snapshot[1], rsp = snapshot[2];
                if (msg == prevCmdValue && arg == prevArgValue) return;
                prevCmdValue = msg; prevArgValue = arg;
                while (list.Count >= MaxRows) list.RemoveAt(0);
                string name = SmuCommandNames.Resolve(nameMap, msg);
                list.Add(new SmuMonitorItem
                {
                    Cmd = name == null ? $"0x{msg:X2}" : $"0x{msg:X2} ({name})",
                    Arg = $"0x{arg:X8}",
                    Rsp = $"0x{rsp:X2} {GetSMUStatus.GetByType((SMU.Status)rsp)}"
                });
                dataGridView2.FirstDisplayedScrollingRowIndex = list.Count - 1;
            }
            catch (Exception ex) { if (!closing) Text = "SMU Monitor — " + ex.Message; }
            finally { refreshing = false; }
        }
        private void SMUMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            closing = true; running = false; generation++;
            timer.Stop(); timer.Dispose();
        }
        private void ButtonClear_Click(object sender, EventArgs e) { generation++; list.Clear(); }
        private void SMUMonitor_Shown(object sender, EventArgs e) { running = true; timer.Start(); }
        private void ButtonApply_Click(object sender, EventArgs e)
        {
            generation++; running = !running;
            buttonStartStop.Text = running ? "Stop" : "Start";
            if (running) { prevCmdValue = 0; timer.Start(); } else timer.Stop();
        }
    }
}
