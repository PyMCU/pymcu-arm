using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Same contract as the RP2040 UF2 artifact test, with the family ID the
/// RP2350 BOOTSEL bootloader expects for a flat picobin ARM-Secure image
/// (0xE48BFF59). A UF2 tagged with the RP2040 family is refused by the board.
/// </summary>
[TestFixture]
public class Uf2ArtifactTests
{
    private const string Example = "blink-rp2350";
    private static byte[] _firmware = null!;
    private static Uf2Image _uf2 = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
    {
        _firmware = PymcuCompiler.BuildRp2350(Example);
        _uf2 = Uf2Image.Load(Path.Combine(
            PymcuCompiler.ExampleDir(Example), "dist", "firmware.uf2"));
    }

    [Test]
    public void EveryBlockCarriesTheUf2Magics()
    {
        _uf2.Blocks.Select(b => b.MagicStart0).Should().AllBeEquivalentTo(Uf2Image.MagicStart0);
        _uf2.Blocks.Select(b => b.MagicStart1).Should().AllBeEquivalentTo(Uf2Image.MagicStart1);
        _uf2.Blocks.Select(b => b.MagicEnd).Should().AllBeEquivalentTo(Uf2Image.MagicEnd);
        _uf2.Blocks.Select(b => b.Flags).Should().AllBeEquivalentTo(Uf2Image.FlagFamilyId);
    }

    [Test]
    public void BlockCountAndAddressesCoverTheFlashImage()
    {
        int expected = (_firmware.Length + Uf2Image.PayloadSize - 1) / Uf2Image.PayloadSize;
        _uf2.Blocks.Count.Should().Be(expected);

        for (int i = 0; i < _uf2.Blocks.Count; i++)
        {
            var b = _uf2.Blocks[i];
            b.BlockNo.Should().Be((uint)i);
            b.NumBlocks.Should().Be((uint)expected);
            b.PayloadSize.Should().Be((uint)Uf2Image.PayloadSize);
            b.TargetAddr.Should().Be(Uf2Image.XipBase + (uint)(i * Uf2Image.PayloadSize));
        }
    }

    [Test]
    public void FamilyIdIsRp2350ArmSecure()
    {
        _uf2.Blocks.Select(b => b.FamilyId).Should().AllBeEquivalentTo(Uf2Image.Rp2350ArmSFamily);
        _uf2.Blocks.Select(b => b.FamilyId).Should().NotContain(Uf2Image.Rp2040Family);
    }

    [Test]
    public void PayloadIsTheFlashImageZeroPadded()
    {
        var payload = _uf2.Payload;
        payload.Take(_firmware.Length).Should().Equal(_firmware);
        payload.Skip(_firmware.Length).Should().AllBeEquivalentTo((byte)0);
    }
}
