using System;
using System.Buffers.Binary;

namespace Ryujinx.HLE.HOS.Services.Hid.Netplay
{
    // Experimental, in-memory patch. Offsets are relative to SSBU 1.0.0's main NSO.
    // This controls the application loop, not every worker or source of randomness.
    internal static class RyuSyncSsbuPatch
    {
        public const string BuildId = "FD471EB458C915E7CD13BBB67420632D00000000000000000000000000000000";
        public const int InputSvc = 0x7F00;
        public const int UpdateSvc = 0x7F01;
        public const int CompleteSvc = 0x7F02;
        public const int InputOffset = 0x278EC1C;
        public const int UpdateOffset = 0x278F07C;
        public const int CompleteOffset = 0x278F084;

        // Validate every site before changing any byte. Retain the catch-up queue's
        // accounting, but remove its extra application call (which reused input).
        internal static readonly (int Offset, uint Expected, uint Replacement)[] Instructions =
        [
            (0x278EC0C, 0xB9430915, 0x52800035), // ldr w21 -> mov w21, #1
            (0x278EC14, 0x540023EB, 0x540023EB),
            (0x278EC18, 0x540006C1, 0x540006C1),
            (InputOffset, 0x3953D328, Svc(InputSvc)),
            (0x278EC20, 0x36001C28, 0x140000E1), // always take the fresh-input path
            (0x278EFA4, 0x97FD59FB, 0x97FD59FB), // original controller poll
            (0x278EFA8, 0x9400149A, 0x9400149A), // original input conversion
            (UpdateOffset, 0xF94147E0, Svc(UpdateSvc)),
            (0x278F080, 0x97FF7970, 0x97FF7970), // original application call
            (CompleteOffset, 0xB944FB48, Svc(CompleteSvc)),
            (0x278F088, 0x11000508, 0x11000508),
            (0x278F08C, 0xB904FB48, 0xB904FB48),
            (0x278F1AC, 0x97FF7925, 0xD503201F), // nop: no second update with cached input
        ];

        public static void Apply(Span<byte> text, uint textOffset, string buildId)
        {
            if (buildId != BuildId || textOffset != 0)
            {
                throw new InvalidOperationException("RyuSync's experimental update synchronization requires the supported SSBU 1.0.0 executable. Disconnect to play other versions.");
            }

            foreach (var instruction in Instructions)
            {
                if (instruction.Offset > text.Length - sizeof(uint) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(text.Slice(instruction.Offset, sizeof(uint))) != instruction.Expected)
                {
                    throw new InvalidOperationException($"RyuSync cannot verify SSBU code at 0x{instruction.Offset:X}. No synchronization patch was applied. Disable conflicting code mods.");
                }
            }

            foreach (var instruction in Instructions)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(text.Slice(instruction.Offset, sizeof(uint)), instruction.Replacement);
            }
        }

        private static uint Svc(int id) => 0xD4000001U | ((uint)id << 5);
    }
}
