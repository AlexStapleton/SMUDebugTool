using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Forms;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    public partial class SMUMonitor : Form
    {
        private readonly Cpu CPU;
        // Poll on a background thread instead of a WinForms timer: the SMU reads no longer
        // run on the dialog's message-loop thread, so the grid/buttons stay responsive even
        // if a ReadDword stalls. Only the row append is marshalled back to the UI thread.
        private const int PollIntervalMs = 10;
        private Thread monitorThread;
        private volatile bool monitorRunning;
        private readonly BindingList<SmuMonitorItem> list = new BindingList<SmuMonitorItem>();
        private uint prevCmdValue;
        private uint prevArgValue;
        // Cap the captured history so a long monitoring session doesn't grow the list (and
        // the grid) without bound. Oldest rows are trimmed once the cap is reached.
        private const int MaxRows = 2000;
        private readonly uint SMU_ADDR_MSG;
        private readonly uint SMU_ADDR_ARG;
        private readonly uint SMU_ADDR_RSP;

        private class SmuMonitorItem
        {
            public string Cmd { get; set; }
            public string Arg { get; set; }
            public string Rsp { get; set; }
        }

        public SMUMonitor(Cpu cpu, uint addrMsg, uint addrArg, uint addrRsp)
        {
            CPU = cpu;
            SMU_ADDR_MSG = addrMsg;
            SMU_ADDR_ARG = addrArg;
            SMU_ADDR_RSP = addrRsp;

            InitializeComponent();

            labelCmdAddr.Text = $"0x{addrMsg:X8}";
            labelRspAddr.Text = $"0x{addrRsp:X8}";
            labelArgAddr.Text = $"0x{addrArg:X8}";

            dataGridView2.DataSource = list;
        }

        private void StartMonitoring()
        {
            if (monitorRunning) return;
            monitorRunning = true;
            monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "SmuMonitor"
            };
            monitorThread.Start();
        }

        private void StopMonitoring()
        {
            // The background thread is IsBackground and exits after its current sleep; no
            // Join so we never block the UI thread for up to a poll interval.
            monitorRunning = false;
        }

        private void MonitorLoop()
        {
            while (monitorRunning)
            {
                try
                {
                    PollOnce();
                }
                catch
                {
                    // A transient read failure shouldn't kill the monitor loop.
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        // Runs on the background thread: all the SMU reads happen here.
        private void PollOnce()
        {
            uint msg = CPU.ReadDword(SMU_ADDR_MSG);
            uint arg = CPU.ReadDword(SMU_ADDR_ARG);

            if (msg == prevCmdValue && arg == prevArgValue)
                return;

            prevCmdValue = msg;
            prevArgValue = arg;

            uint rsp = CPU.ReadDword(SMU_ADDR_RSP);
            if (rsp != 0)
                arg = CPU.ReadDword(SMU_ADDR_ARG);

            var item = new SmuMonitorItem
            {
                Cmd = $"0x{msg:X2}",
                Arg = $"0x{arg:X8}",
                Rsp = $"0x{rsp:X2} {GetSMUStatus.GetByType((SMU.Status)rsp)}"
            };

            AppendRow(item);
        }

        // Marshals the row append onto the UI thread, tolerating a closing form.
        private void AppendRow(SmuMonitorItem item)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    while (list.Count >= MaxRows)
                        list.RemoveAt(0);

                    list.Add(item);
                    dataGridView2.FirstDisplayedScrollingRowIndex = list.Count - 1;
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SMUMonitor_FormClosing(object sender, FormClosingEventArgs e) => StopMonitoring();

        private void ButtonClear_Click(object sender, EventArgs e) => list.Clear();

        private void SMUMonitor_Shown(object sender, EventArgs e) => StartMonitoring();

        private void ButtonApply_Click(object sender, EventArgs e)
        {
            if (monitorRunning)
            {
                StopMonitoring();
                buttonStartStop.Text = "Start";
            }
            else
            {
                prevCmdValue = 0;
                StartMonitoring();
                buttonStartStop.Text = "Stop";
            }
        }
    }
}
