namespace PyMCU.IntegrationTests;

/// <summary>
/// Reader for the UF2 file the LLVM toolchain packs next to firmware.bin
/// (see src/python/pymcu/toolchain/rp2040/uf2.py). A UF2 is a stream of
/// 512-byte blocks; the BOOTSEL bootloader rejects the image outright if a
/// magic, the family ID or the block accounting is wrong, so tests assert the
/// structure rather than booting it.
/// </summary>
public sealed class Uf2Image
{
    public const uint MagicStart0 = 0x0A324655;
    public const uint MagicStart1 = 0x9E5D5157;
    public const uint MagicEnd = 0x0AB16F30;
    public const uint FlagFamilyId = 0x00002000;

    public const uint XipBase = 0x10000000;
    public const int BlockSize = 512;
    public const int PayloadSize = 256;

    // picotool/pico-sdk family IDs (boot/uf2.h).
    public const uint Rp2040Family = 0xE48BFF56;
    public const uint Rp2350ArmSFamily = 0xE48BFF59;

    public sealed record Block(
        uint MagicStart0, uint MagicStart1, uint Flags, uint TargetAddr,
        uint PayloadSize, uint BlockNo, uint NumBlocks, uint FamilyId,
        byte[] Data, uint MagicEnd);

    public IReadOnlyList<Block> Blocks { get; }

    private Uf2Image(IReadOnlyList<Block> blocks) => Blocks = blocks;

    /// <summary>Concatenated block payloads, i.e. the flash image plus zero padding.</summary>
    public byte[] Payload => Blocks.SelectMany(b => b.Data).ToArray();

    public static Uf2Image Load(string path) => Parse(File.ReadAllBytes(path));

    public static Uf2Image Parse(byte[] raw)
    {
        if (raw.Length == 0 || raw.Length % BlockSize != 0)
            throw new InvalidDataException(
                $"UF2 length must be a non-zero multiple of {BlockSize}, got {raw.Length}");

        var blocks = new List<Block>(raw.Length / BlockSize);
        for (int o = 0; o < raw.Length; o += BlockSize)
        {
            var data = new byte[PayloadSize];
            Array.Copy(raw, o + 32, data, 0, PayloadSize);
            blocks.Add(new Block(
                U32(raw, o), U32(raw, o + 4), U32(raw, o + 8), U32(raw, o + 12),
                U32(raw, o + 16), U32(raw, o + 20), U32(raw, o + 24), U32(raw, o + 28),
                data, U32(raw, o + BlockSize - 4)));
        }
        return new Uf2Image(blocks);
    }

    private static uint U32(byte[] b, int offset) => BitConverter.ToUInt32(b, offset);
}
