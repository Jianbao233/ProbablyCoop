using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;

namespace ProbablyCoop;

// Used only to audit original method bodies before applying the actor patches.
internal static class NativeIl
{
    private static readonly Dictionary<short, OpCode> Opcodes = new[]
    {
        OpCodes.Add, OpCodes.Add_Ovf, OpCodes.Add_Ovf_Un, OpCodes.And,
        OpCodes.Arglist, OpCodes.Beq, OpCodes.Beq_S, OpCodes.Bge,
        OpCodes.Bge_S, OpCodes.Bge_Un, OpCodes.Bge_Un_S, OpCodes.Bgt,
        OpCodes.Bgt_S, OpCodes.Bgt_Un, OpCodes.Bgt_Un_S, OpCodes.Ble,
        OpCodes.Ble_S, OpCodes.Ble_Un, OpCodes.Ble_Un_S, OpCodes.Blt,
        OpCodes.Blt_S, OpCodes.Blt_Un, OpCodes.Blt_Un_S, OpCodes.Bne_Un,
        OpCodes.Bne_Un_S, OpCodes.Box, OpCodes.Br, OpCodes.Br_S,
        OpCodes.Break, OpCodes.Brfalse, OpCodes.Brfalse_S, OpCodes.Brtrue,
        OpCodes.Brtrue_S, OpCodes.Call, OpCodes.Calli, OpCodes.Callvirt,
        OpCodes.Castclass, OpCodes.Ceq, OpCodes.Cgt, OpCodes.Cgt_Un,
        OpCodes.Ckfinite, OpCodes.Clt, OpCodes.Clt_Un, OpCodes.Constrained,
        OpCodes.Conv_I, OpCodes.Conv_I1, OpCodes.Conv_I2, OpCodes.Conv_I4,
        OpCodes.Conv_I8, OpCodes.Conv_Ovf_I, OpCodes.Conv_Ovf_I_Un, OpCodes.Conv_Ovf_I1,
        OpCodes.Conv_Ovf_I1_Un, OpCodes.Conv_Ovf_I2, OpCodes.Conv_Ovf_I2_Un, OpCodes.Conv_Ovf_I4,
        OpCodes.Conv_Ovf_I4_Un, OpCodes.Conv_Ovf_I8, OpCodes.Conv_Ovf_I8_Un, OpCodes.Conv_Ovf_U,
        OpCodes.Conv_Ovf_U_Un, OpCodes.Conv_Ovf_U1, OpCodes.Conv_Ovf_U1_Un, OpCodes.Conv_Ovf_U2,
        OpCodes.Conv_Ovf_U2_Un, OpCodes.Conv_Ovf_U4, OpCodes.Conv_Ovf_U4_Un, OpCodes.Conv_Ovf_U8,
        OpCodes.Conv_Ovf_U8_Un, OpCodes.Conv_R_Un, OpCodes.Conv_R4, OpCodes.Conv_R8,
        OpCodes.Conv_U, OpCodes.Conv_U1, OpCodes.Conv_U2, OpCodes.Conv_U4,
        OpCodes.Conv_U8, OpCodes.Cpblk, OpCodes.Cpobj, OpCodes.Div,
        OpCodes.Div_Un, OpCodes.Dup, OpCodes.Endfilter, OpCodes.Endfinally,
        OpCodes.Initblk, OpCodes.Initobj, OpCodes.Isinst, OpCodes.Jmp,
        OpCodes.Ldarg, OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2,
        OpCodes.Ldarg_3, OpCodes.Ldarg_S, OpCodes.Ldarga, OpCodes.Ldarga_S,
        OpCodes.Ldc_I4, OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2,
        OpCodes.Ldc_I4_3, OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6,
        OpCodes.Ldc_I4_7, OpCodes.Ldc_I4_8, OpCodes.Ldc_I4_M1, OpCodes.Ldc_I4_S,
        OpCodes.Ldc_I8, OpCodes.Ldc_R4, OpCodes.Ldc_R8, OpCodes.Ldelem,
        OpCodes.Ldelem_I, OpCodes.Ldelem_I1, OpCodes.Ldelem_I2, OpCodes.Ldelem_I4,
        OpCodes.Ldelem_I8, OpCodes.Ldelem_R4, OpCodes.Ldelem_R8, OpCodes.Ldelem_Ref,
        OpCodes.Ldelem_U1, OpCodes.Ldelem_U2, OpCodes.Ldelem_U4, OpCodes.Ldelema,
        OpCodes.Ldfld, OpCodes.Ldflda, OpCodes.Ldftn, OpCodes.Ldind_I,
        OpCodes.Ldind_I1, OpCodes.Ldind_I2, OpCodes.Ldind_I4, OpCodes.Ldind_I8,
        OpCodes.Ldind_R4, OpCodes.Ldind_R8, OpCodes.Ldind_Ref, OpCodes.Ldind_U1,
        OpCodes.Ldind_U2, OpCodes.Ldind_U4, OpCodes.Ldlen, OpCodes.Ldloc,
        OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3,
        OpCodes.Ldloc_S, OpCodes.Ldloca, OpCodes.Ldloca_S, OpCodes.Ldnull,
        OpCodes.Ldobj, OpCodes.Ldsfld, OpCodes.Ldsflda, OpCodes.Ldstr,
        OpCodes.Ldtoken, OpCodes.Ldvirtftn, OpCodes.Leave, OpCodes.Leave_S,
        OpCodes.Localloc, OpCodes.Mkrefany, OpCodes.Mul, OpCodes.Mul_Ovf,
        OpCodes.Mul_Ovf_Un, OpCodes.Neg, OpCodes.Newarr, OpCodes.Newobj,
        OpCodes.Nop, OpCodes.Not, OpCodes.Or, OpCodes.Pop,
        OpCodes.Prefix2, OpCodes.Prefix3, OpCodes.Prefix4,
        OpCodes.Prefix5, OpCodes.Prefix6, OpCodes.Prefix7, OpCodes.Prefixref,
        OpCodes.Readonly, OpCodes.Refanytype, OpCodes.Refanyval, OpCodes.Rem,
        OpCodes.Rem_Un, OpCodes.Ret, OpCodes.Rethrow, OpCodes.Shl,
        OpCodes.Shr, OpCodes.Shr_Un, OpCodes.Sizeof, OpCodes.Starg,
        OpCodes.Starg_S, OpCodes.Stelem, OpCodes.Stelem_I, OpCodes.Stelem_I1,
        OpCodes.Stelem_I2, OpCodes.Stelem_I4, OpCodes.Stelem_I8, OpCodes.Stelem_R4,
        OpCodes.Stelem_R8, OpCodes.Stelem_Ref, OpCodes.Stfld, OpCodes.Stind_I,
        OpCodes.Stind_I1, OpCodes.Stind_I2, OpCodes.Stind_I4, OpCodes.Stind_I8,
        OpCodes.Stind_R4, OpCodes.Stind_R8, OpCodes.Stind_Ref, OpCodes.Stloc,
        OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3,
        OpCodes.Stloc_S, OpCodes.Stobj, OpCodes.Stsfld, OpCodes.Sub,
        OpCodes.Sub_Ovf, OpCodes.Sub_Ovf_Un, OpCodes.Switch, OpCodes.Tailcall,
        OpCodes.Throw, OpCodes.Unaligned, OpCodes.Unbox, OpCodes.Unbox_Any,
        OpCodes.Volatile, OpCodes.Xor,
    }.ToDictionary(opcode => opcode.Value);

    internal readonly struct Instruction
    {
        internal readonly OpCode Opcode;
        internal readonly int Offset;
        internal readonly int OperandOffset;

        internal Instruction(OpCode opcode, int offset, int operandOffset)
        {
            Opcode = opcode;
            Offset = offset;
            OperandOffset = operandOffset;
        }
    }

    internal static IEnumerable<Instruction> Read(byte[] bytes)
    {
        int position = 0;
        while (position < bytes.Length)
        {
            int offset = position;
            short code = bytes[position++];
            if (code == 0xfe)
            {
                RequireBytes(bytes, position, 1);
                code = unchecked((short)(0xfe00 | bytes[position++]));
            }
            if (!Opcodes.TryGetValue(code, out var opcode))
                throw new InvalidDataException("Unknown native IL opcode at " + offset);

            int length = OperandLength(opcode.OperandType, bytes, position);
            RequireBytes(bytes, position, length);
            yield return new Instruction(opcode, offset, position);
            position += length;
        }
    }

    private static int OperandLength(OperandType operandType, byte[] bytes, int position)
    {
        switch (operandType)
        {
            case OperandType.InlineNone: return 0;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar: return 1;
            case OperandType.InlineVar: return 2;
            case OperandType.InlineI8:
            case OperandType.InlineR: return 8;
            case OperandType.InlineSwitch:
                RequireBytes(bytes, position, 4);
                int count = BitConverter.ToInt32(bytes, position);
                if (count < 0 || count > (bytes.Length - position - 4) / 4)
                    throw new InvalidDataException("Invalid native IL switch");
                return 4 + count * 4;
            default: return 4;
        }
    }

    private static void RequireBytes(byte[] bytes, int offset, int count)
    {
        if (count < 0 || offset > bytes.Length - count)
            throw new InvalidDataException("Truncated native IL operand");
    }
}
