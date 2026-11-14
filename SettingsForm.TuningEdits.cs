using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private TuningEditControls editControls;
        private Button applyChangesButton, discardEditsButton;
        private Label pendingEditsLabel;
        private void InitTuningEdits()
        {
            editControls = new TuningEditControls();
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0 || cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0)
                editControls.RegisterCores(coControls, hardware.Topology.Targets.Select(t => t.Slot));
            if (_fmaxSupported) editControls.Register(TuningKind.Fmax, 0, numericUpDownFmax);
            if (_curveShaperSupported)
                for (int tier = 0; tier < 5; tier++) editControls.Register(TuningKind.CurveShaper, tier, CsGrid[tier]);
            editControls.Register(TuningKind.Ppt, 0, numericUpDownPpt);
            editControls.Register(TuningKind.Tdc, 0, numericUpDownTdc);
            editControls.Register(TuningKind.Edc, 0, numericUpDownEdc);
            editControls.Register(TuningKind.Scalar, 0, numericUpDownPboScalar);
            editControls.Changed += UpdateEditActions;
            Disposed += (s, e) => editControls.Dispose();
            UpdateEditActions();
        }
        private void UpdateEditActions()
        {
            if (editControls == null) return;
            applyChangesButton.Enabled = _hardwareReady && !hardwareOperation && editControls.PendingCount > 0;
            discardEditsButton.Enabled = !hardwareOperation && editControls.CanDiscard;
            pendingEditsLabel.Text = string.Format(UiText.T("{0} pending settings. Yellow fields are local edits."), editControls.PendingCount);
        }
        private async void ApplyChanges_Click(object sender, EventArgs e)
        {
            await ApplyTuningAsync(editControls.Session.Changes(GatherProfileFromUi(CurrentProfileName)), "Apply changes");
        }
        private async void PreviewChanges_Click(object sender, EventArgs e)
        {
            if (!_hardwareReady || shuttingDown || hardwareOperation) { SetStatusText("Hardware is still loading or busy."); return; }
            var profile = editControls.Session.Changes(GatherProfileFromUi(CurrentProfileName));
            if (TuningService.RequestsFromProfile(profile).Count == 0) { SetStatusText(UiText.T("No pending changes to apply.")); return; }
            using (var preview = new TuningPreviewForm(profile, editControls.Session, "Apply changes"))
                if (preview.ShowDialog(this) == DialogResult.OK) await ApplyTuningAsync(preview.ApprovedProfile, "Apply changes");
        }
        private void DiscardEdits_Click(object sender, EventArgs e)
        {
            editControls.Discard();
            SetStatusText(UiText.T("Edits discarded locally; no commands sent."));
        }
    }
}
