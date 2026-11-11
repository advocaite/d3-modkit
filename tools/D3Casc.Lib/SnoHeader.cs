using System.Buffers.Binary;

namespace D3Casc;

/// <summary>
/// The 28-byte header every SNO file starts with, named like the emulator's
/// Core/MPQ/FileFormats/Types/Common.cs <c>Header</c> (DeadBeef, SnoType, Unknown1, Unknown2, SNOId, Unknown3, Unknown4).
/// </summary>
public readonly record struct SnoHeader(int DeadBeef, int SnoType, int Unknown1, int Unknown2, int SnoId, int Unknown3, int Unknown4)
{
    public const int Size = 28;
    public const uint Magic = 0xDEADBEEF;

    public bool IsValid => (uint)DeadBeef == Magic;

    public static bool TryParse(ReadOnlySpan<byte> data, out SnoHeader header)
    {
        header = default;
        if (data.Length < Size) return false;
        header = new SnoHeader(
            BinaryPrimitives.ReadInt32LittleEndian(data),
            BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[24..]));
        return header.IsValid;
    }

    /// <summary>Names of the fields that differ between two headers.</summary>
    public static IEnumerable<string> Diff(SnoHeader a, SnoHeader b)
    {
        if (a.SnoType != b.SnoType) yield return "SnoType";
        if (a.Unknown1 != b.Unknown1) yield return "Unknown1";
        if (a.Unknown2 != b.Unknown2) yield return "Unknown2";
        if (a.SnoId != b.SnoId) yield return "SNOId";
        if (a.Unknown3 != b.Unknown3) yield return "Unknown3";
        if (a.Unknown4 != b.Unknown4) yield return "Unknown4";
    }

    public override string ToString() => $"type={SnoType} id={SnoId} u1=0x{Unknown1:X} u2=0x{Unknown2:X} u3=0x{Unknown3:X} u4=0x{Unknown4:X}";
}
