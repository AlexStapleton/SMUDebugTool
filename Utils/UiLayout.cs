using System.Drawing;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public static class UiLayout
    {
        public static void Configure(Form form, Size minimum)
        {
            if (form.AutoScaleMode == AutoScaleMode.None || form.AutoScaleMode == AutoScaleMode.Inherit)
            {
                form.AutoScaleDimensions = new SizeF(96, 96);
                form.AutoScaleMode = AutoScaleMode.Dpi;
            }
            form.MinimumSize = minimum;
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.MaximizeBox = true;
            form.Shown += (s, e) => UiText.Apply(form);
        }
    }
}
