using Iced.Intel;

namespace Armory;

internal static class MemoryUtilities
{
    public static unsafe (bool IsRead, bool IsWrite) AnalyzeInstructionAccess(nint instructionAddress)
    {
        var codeBytes = new ReadOnlySpan<byte>((void*) instructionAddress, 15).ToArray();

        var decoder = Decoder.Create(64, new ByteArrayCodeReader(codeBytes));
        decoder.IP = (ulong) instructionAddress;

        var instruction = decoder.Decode();

        if (instruction.IsInvalid)
        {
            return (false, false);
        }

        var info = new InstructionInfoFactory().GetInfo(instruction);

        var isRead  = false;
        var isWrite = false;

        foreach (var memUse in info.GetUsedMemory())
        {
            switch (memUse.Access)
            {
                case OpAccess.Read:
                case OpAccess.CondRead:
                    isRead = true;

                    break;
                case OpAccess.Write:
                case OpAccess.CondWrite:
                    isWrite = true;

                    break;
                case OpAccess.ReadWrite:
                    isRead  = true;
                    isWrite = true;

                    break;
            }
        }

        return (isRead, isWrite);
    }
}
