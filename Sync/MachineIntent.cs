using System;

namespace ProbablyCoop.Sync;

internal enum MachineControl : byte { Button = 1, Radio, Slider, Range, Toggle, Recipe }

internal readonly struct MachineIntent
{
    internal readonly int Owner, Index;
    internal readonly MachineControl Control;
    internal readonly ulong Proof;
    internal readonly float First, Second;
    internal readonly string Choice;

    internal MachineIntent(int owner, int index, MachineControl control, ulong proof,
        float first = 0, float second = 0, string choice = "")
    {
        Owner = owner;
        Index = index;
        Control = control;
        Proof = proof;
        First = first;
        Second = second;
        Choice = choice;
    }

    internal bool Valid => Owner > 0 && Index is >= 0 and < 256 && Proof != 0 &&
        Control is >= MachineControl.Button and <= MachineControl.Recipe &&
        !float.IsNaN(First) && !float.IsInfinity(First) && !float.IsNaN(Second) && !float.IsInfinity(Second) &&
        (Control == MachineControl.Range ? Second >= First : Second == 0) &&
        (Control == MachineControl.Radio ? First is 0 or 1 :
            Control is MachineControl.Slider or MachineControl.Range || First == 0) &&
        (Control == MachineControl.Recipe ? Index == 0 && Choice?.Length <= 96 && TeamTransition.ValidLocation(Choice) :
            string.IsNullOrEmpty(Choice) && (Control != MachineControl.Toggle || Index == 0));

    internal void Write(SyncWriter writer)
    {
        writer.UInt((uint)Owner);
        writer.Byte((byte)Index);
        writer.Byte((byte)Control);
        writer.ULong(Proof);
        writer.UInt(unchecked((uint)BitConverter.SingleToInt32Bits(First)));
        writer.UInt(unchecked((uint)BitConverter.SingleToInt32Bits(Second)));
        if (Control == MachineControl.Recipe) writer.Text(Choice);
    }

    internal static MachineIntent Read(SyncReader reader)
    {
        uint owner = reader.UInt();
        int index = reader.Byte();
        var control = (MachineControl)reader.Byte();
        ulong proof = reader.ULong();
        float first = BitConverter.Int32BitsToSingle(unchecked((int)reader.UInt()));
        float second = BitConverter.Int32BitsToSingle(unchecked((int)reader.UInt()));
        string choice = control == MachineControl.Recipe ? reader.Text(96) : "";
        return owner <= int.MaxValue ? new MachineIntent((int)owner, index, control, proof, first, second, choice) : default;
    }
}
