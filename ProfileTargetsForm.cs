using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public sealed class ProfileTargetsForm : Form
    {
        public Profile ReviewedProfile { get; private set; }
        public ProfileTargetsForm(Profile profile, TopologySnapshot topology)
        {
            UiLayout.Configure(this, new Size(650, 350));
            Text = "Review profile core targets"; Width = 850; Height = 420;
            var review = new ProfileTargetReview(profile, topology);
            var explanation = new Label
            {
                Dock = DockStyle.Top,
                Height = 64,
                Text = "The saved topology differs. Review every requested physical target. Loading changes only the controls; Apply and Save remain separate actions. Unresolved or duplicate targets cannot be converted."
            };
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                DataSource = review.Rows.ToList(),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
            };
            var load = new Button { Dock = DockStyle.Bottom, Height = 32, Text = "Load reviewed bindings", Enabled = review.CanConvert };
            load.Click += (s, e) => { ReviewedProfile = review.ConvertAfterReview(); DialogResult = DialogResult.OK; Close(); };
            Controls.Add(grid); Controls.Add(explanation); Controls.Add(load);
        }
    }
}
