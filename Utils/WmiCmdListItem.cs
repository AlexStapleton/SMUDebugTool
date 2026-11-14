namespace ZenStatesDebugTool
{
    public class WmiCmdListItem
    {
        public uint value { get; }
        public string text { get; }

        public bool isSet { get; }
        public uint? PropertyFlag { get; }
        private readonly bool friendly;
        private AcpiCommandHelp help;
        public AcpiCommandHelp Help => help ?? (help = AcpiCommandHelp.For(text, value, isSet));

        public WmiCmdListItem(string text, uint value, bool isSet = false, uint? propertyFlag = null, bool friendly = false)
        {
            this.text = text;
            this.value = value;
            this.isSet = isSet;
            PropertyFlag = propertyFlag; this.friendly = friendly;
        }

        public override string ToString()
        {
            return friendly ? Help.DisplayName : this.text;
        }
    }
}
