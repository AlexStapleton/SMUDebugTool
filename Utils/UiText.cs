using System;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public static class UiText
    {
        private static readonly ResourceManager resources = new ResourceManager("ZenStatesDebugTool.Resources.UiStrings", typeof(UiText).Assembly);
        private sealed class Caption { public string English, Rendered; }
        private static readonly ConditionalWeakTable<object, Caption> captions = new ConditionalWeakTable<object, Caption>();
        public static string Language => Thread.CurrentThread.CurrentUICulture.Name == "zh-CN" ? "zh-CN" : "en";
        public static void SetLanguage(string language)
        {
            var culture = CultureInfo.GetCultureInfo(language == "zh-CN" ? "zh-CN" : "en");
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        public static string T(string english)
            => T(english, Thread.CurrentThread.CurrentUICulture);
        public static string T(string english, CultureInfo culture)
        {
            if (string.IsNullOrEmpty(english)) return english;
            string value = resources.GetString(english, culture);
            if (value != null) return value;
            var core = Regex.Match(english, @"^Core (\d+)$");
            return core.Success ? string.Format(culture, resources.GetString("Core {0}", culture) ?? "Core {0}", core.Groups[1].Value) : english;
        }
        private static string CaptionFor(object control, string current)
        {
            if (captions.TryGetValue(control, out Caption existing))
            {
                if (current == existing.Rendered) return existing.Rendered = T(existing.English);
                captions.Remove(control); // A live status/value replaced the original caption.
            }
            if (T(current) == current && resources.GetString(current ?? "") == null) return current;
            captions.Add(control, new Caption { English = current, Rendered = T(current) });
            return T(current);
        }
        public static void Apply(Control root)
        {
            if (!(root is TextBoxBase) && !(root is NumericUpDown) && !(root is ComboBox)) root.Text = CaptionFor(root, root.Text);
            if (root is ToolStrip strip) foreach (ToolStripItem item in strip.Items) TranslateMenu(item);
            if (root is DataGridView grid)
                foreach (DataGridViewColumn column in grid.Columns)
                    if (!string.IsNullOrEmpty(column.DataPropertyName)) column.HeaderText = T(column.DataPropertyName);
            foreach (Control child in root.Controls) Apply(child);
        }
        private static void TranslateMenu(ToolStripItem item)
        {
            item.Text = CaptionFor(item, item.Text);
            if (item is ToolStripDropDownItem menu) foreach (ToolStripItem child in menu.DropDownItems) TranslateMenu(child);
        }
    }
}
