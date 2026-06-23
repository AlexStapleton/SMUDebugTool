using System;
using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    public enum RegisterKind { Msr, Pci, Cpuid }

    // Optional helpers a derived line may need (e.g. generation-aware VID->voltage).
    // Supplied by the caller; when a needed delegate is null the derived line is skipped.
    public sealed class DecodeContext
    {
        public Func<uint, double> VidToVoltage { get; set; }
        public static readonly DecodeContext None = new DecodeContext();
    }

    // A named raw bit-field [HighBit:LowBit] within the register value.
    public sealed class FieldDefinition
    {
        public string Name { get; }
        public int HighBit { get; }
        public int LowBit { get; }

        public FieldDefinition(string name, int highBit, int lowBit)
        {
            Name = name;
            HighBit = highBit;
            LowBit = lowBit;
        }
    }

    // A recognized register: friendly name, raw bit-fields, and optional derived
    // lines (e.g. "Frequency: 4200 MHz") computed from the whole value + context.
    public sealed class RegisterDefinition
    {
        public RegisterKind Kind { get; }
        public uint Address { get; }
        public string Name { get; }
        public string Description { get; }
        public IReadOnlyList<FieldDefinition> Fields { get; }
        public IReadOnlyList<Func<ulong, DecodeContext, string>> Derived { get; }

        public RegisterDefinition(
            RegisterKind kind, uint address, string name, string description,
            IReadOnlyList<FieldDefinition> fields,
            IReadOnlyList<Func<ulong, DecodeContext, string>> derived = null)
        {
            Kind = kind;
            Address = address;
            Name = name;
            Description = description;
            Fields = fields ?? new List<FieldDefinition>();
            Derived = derived ?? new List<Func<ulong, DecodeContext, string>>();
        }
    }
}
