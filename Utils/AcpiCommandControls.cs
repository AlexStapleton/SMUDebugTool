using System;
using System.Linq;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class AcpiCommandControls
    {
        private sealed class Choice
        {
            public uint Raw; public AcpiCommandHelp Info;
            public override string ToString() => Info.Format(Raw) + " " + UiText.T(Info.Unit);
        }
        private readonly ComboBox choices;
        private readonly TextBox input;
        private readonly Button send;
        private readonly Label choiceCaption, argumentCaption;
        public Label Description { get; } = new Label { AutoSize = true };
        public Label Domain { get; } = new Label { AutoSize = true };
        public Label Current { get; } = new Label { AutoSize = true };
        public Label Hint { get; } = new Label { AutoSize = true };
        public Label Validation { get; } = new Label { AutoSize = true };
        public Label Result { get; } = new Label { AutoSize = true };
        public AcpiCommandHelp Info { get; private set; }
        public AcpiValueMetadata Metadata { get; private set; }
        public AcpiCommandControls(TableLayoutPanel table, ComboBox commands, ComboBox choices, TextBox input, Button send)
        {
            this.choices = choices; this.input = input; this.send = send;
            table.SuspendLayout(); table.Controls.Clear(); table.ColumnStyles.Clear(); table.RowStyles.Clear();
            table.ColumnCount = 2; table.RowCount = 10; table.AutoSize = true; table.AutoSizeMode = AutoSizeMode.GrowAndShrink; table.Dock = DockStyle.Top;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < table.RowCount; row++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) };
            table.Controls.Add(Caption("Setting"), 0, 0); table.Controls.Add(commands, 1, 0);
            choiceCaption = Caption("Firmware choices"); argumentCaption = Caption("New value");
            table.Controls.Add(choiceCaption, 0, 4); table.Controls.Add(choices, 1, 4);
            table.Controls.Add(argumentCaption, 0, 5); table.Controls.Add(input, 1, 5);
            foreach (var pair in new[] { Tuple.Create(Description, 1), Tuple.Create(Domain, 2), Tuple.Create(Current, 3), Tuple.Create(Hint, 6), Tuple.Create(Validation, 7), Tuple.Create(Result, 9) }) {
                pair.Item1.Dock = DockStyle.Top; pair.Item1.Margin = new Padding(3, 4, 3, 4);
                table.Controls.Add(pair.Item1, 0, pair.Item2); table.SetColumnSpan(pair.Item1, 2);
            }
            commands.Dock = choices.Dock = input.Dock = DockStyle.Fill;
            commands.DropDownWidth = 550;
            send.AutoSize = true; send.Anchor = AnchorStyles.Left; table.Controls.Add(send, 1, 8);
            input.TextChanged += (s, e) => ValidateInput();
            void Wrap() {
                foreach (var label in new[] { Description, Domain, Current, Hint, Validation, Result })
                    label.MaximumSize = new System.Drawing.Size(Math.Max(100, table.ClientSize.Width - 12), 0);
            }
            table.SizeChanged += (s, e) => Wrap(); Wrap(); table.ResumeLayout(true);
            send.Enabled = false;
        }
        public void SetCommand(AcpiCommandHelp info)
        {
            Info = info; Metadata = AcpiValueMetadata.Unknown(info.Id, "Reading firmware input limits...");
            input.Text = ""; Result.Text = "";
            Description.Text = UiText.T(info.Description); Hint.Text = UiText.T(info.Example);
            argumentCaption.Text = UiText.T("New value") + " (" + UiText.T(info.Unit) + ")";
            // Long unit names belong in the guidance, not a narrow label column.
            argumentCaption.MaximumSize = new System.Drawing.Size(115, 0);
            send.Text = UiText.T(info.IsSet ? "Apply firmware setting" : "Read firmware");
            ShowMetadata(Metadata);
        }
        public void ShowMetadata(AcpiValueMetadata metadata)
        {
            if (Info == null || metadata.Id != Info.Id) return;
            Metadata = metadata; choices.Items.Clear();
            foreach (uint value in metadata.Choices) choices.Items.Add(new Choice { Raw = value, Info = Info });
            bool hasChoices = Info.IsSet && choices.Items.Count > 0;
            choices.Visible = choiceCaption.Visible = hasChoices;
            choices.Enabled = hasChoices;
            Domain.Text = metadata.HasRange ? string.Format(UiText.T("Firmware input range: {0} to {1}; step {2} {3}."), Info.Format(metadata.Min), Info.Format(metadata.Max), Info.Format(metadata.Step), UiText.T(Info.Unit))
                : metadata.Choices.Count > 0 ? UiText.T("Choose a firmware-listed value, or type an equivalent value below.")
                : UiText.T(metadata.Note ?? "Firmware did not report allowed values.");
            Current.Text = UiText.T("Firmware value (snapshot)") + ": " + (metadata.Current.HasValue ? Info.Format(metadata.Current.Value) + " " + UiText.T(Info.Unit) : UiText.T("Not reported"));
            input.Enabled = Info.IsSet;
            ValidateInput();
        }
        public void SelectChoice()
        {
            if (choices.SelectedItem is Choice choice) input.Text = Info.Format(choice.Raw);
        }
        public bool TryArgument(out uint value, out string error)
        {
            value = 0; error = null;
            return Info != null && (!Info.IsSet || Info.TryArgument(input.Text, Metadata, out value, out error));
        }
        private void ValidateInput()
        {
            if (Info == null) { send.Enabled = false; return; }
            bool valid = TryArgument(out uint value, out string error);
            send.Enabled = valid;
            Validation.Text = !Info.IsSet ? UiText.T("No input value is needed for this read command.") : valid
                ? UiText.T("Requested value") + ": " + Info.Format(value) + " " + UiText.T(Info.Unit)
                : input.Text.Trim().Length == 0 ? UiText.T("Enter a value in the displayed unit.") : error;
        }
    }
}
