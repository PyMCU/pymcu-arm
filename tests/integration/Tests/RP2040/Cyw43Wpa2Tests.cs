using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using RP2040Sharp.Wireless.Cyw43;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// WPA2-PSK association on the Pico W, from examples/wifi-wpa2-rp2040.
///
/// WPA2 on this part is not cryptography on the host: the four-way handshake runs in the
/// CYW43439's own firmware, so the driver's job is ten ioctls in a fixed order ending in the
/// same WLC_SET_SSID an open join sends.
///
/// WHY THE ORDER IS ASSERTED AND NOT JUST THE JOIN. The emulator's association state machine
/// decides with the SSID and the passphrase alone: it has no branch for WLC_SET_WSEC (134),
/// WLC_SET_INFRA (20), WLC_SET_AUTH (22) or WLC_SET_WPA_AUTH (165), and the bsscfg iovars are
/// ACKed and recorded as ignored. **A join here would come out green with those four omitted.**
/// So a test that asserts only "it joined" cannot tell a complete sequence from one the
/// emulator forgives, and the thing silicon will punish is exactly the missing half.
///
/// What that leaves for the board on the day: the emulator settles the frame layout, the
/// order and the bytes; the Pico W settles whether the sequence is COMPLETE. One question
/// rather than "let us see if it works".
/// </summary>
[TestFixture]
public class Cyw43Wpa2Tests
{
    private byte[] _firmware = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _firmware = PymcuCompiler.BuildRp2040("wifi-wpa2-rp2040");

    /// The ten sends the reference driver makes for CYW43_AUTH_WPA2_AES_PSK with no BSSID
    /// and no channel, in order. An iovar is WLC_SET_VAR (263) plus the variable's name.
    private static readonly (uint Cmd, string Var)[] Expected =
    {
        (134u, ""),                        // WLC_SET_WSEC, wsec = 4 (AES)
        (263u, "bsscfg:sup_wpa"),          // hand the association to the chip's supplicant
        (263u, "bsscfg:sup_wpa2_eapver"),
        (263u, "bsscfg:sup_wpa_tmo"),
        (268u, ""),                        // WLC_SET_WSEC_PMK, the passphrase
        (20u,  ""),                        // WLC_SET_INFRA
        (22u,  ""),                        // WLC_SET_AUTH, AUTH_TYPE_OPEN
        (263u, "mfp"),
        (165u, ""),                        // WLC_SET_WPA_AUTH, WPA2 PSK
        (26u,  ""),                        // WLC_SET_SSID, the join itself
    };

    private (List<(uint, string)> Sent, string? Joined) Run(string apPassword, string keyInFirmware)
    {
        using var pico = PicoWSimulation.Create(_firmware);
        pico.OfferAp("RP2350Sharp-AP", password: apPassword);

        var sent = new List<(uint, string)>();
        string? joined = null;
        pico.Wifi.OnIoctl += (cmd, kind, varName, inLen) => sent.Add((cmd, varName));
        pico.Wifi.OnStaJoin += s => joined = s;

        for (int i = 0; i < 400_000 && joined == null; i++) pico.Step();
        for (int i = 0; i < 40_000; i++) pico.Step();
        return (sent, joined);
    }

    [Test]
    public void TheTenSendsArriveInOrder()
    {
        var (sent, _) = Run("hunter2", "hunter2");

        // Subsequence rather than equality: bring-up and the post-join drain issue ioctls of
        // their own, and pinning the whole list would fail on an unrelated change to either.
        int at = 0;
        foreach (var got in sent)
        {
            if (at < Expected.Length && got == Expected[at]) at++;
        }

        at.Should().Be(Expected.Length,
            "the WPA2 join is ten ioctls in a fixed order and the emulator does not enforce " +
            $"nine of them; got as far as index {at}: " +
            string.Join(" ", sent.Select(s => s.Item2.Length > 0 ? s.Item2 : s.Item1.ToString())));
    }

    [Test]
    public void TheRightPassphraseJoins()
    {
        var (_, joined) = Run("hunter2", "hunter2");
        joined.Should().Be("RP2350Sharp-AP", "the passphrase the firmware sends matches the AP's");
    }

    [Test]
    public void AWrongPassphraseDoesNotJoin_soTheRightOneIsEvidence()
    {
        // This is the instrument's control, not an extra case. If the AP accepted any
        // passphrase, TheRightPassphraseJoins would pass with the PMK frame built wrong, and
        // the emulator would be certifying a field it never read.
        var (_, joined) = Run("not-hunter2", "hunter2");
        joined.Should().BeNull("a mismatched PSK must stall the supplicant instead of joining");
    }

    /// <summary>Every F2 packet carries an SDPCM sequence number in byte 4, and it advances.
    /// It is half the chip's bus credit flow control: the chip grants credit ahead of the
    /// host's last sequence, so a host that never advances is asking for the same credit
    /// forever. The emulator READS byte 4 -- it tracks it to decide what credit to grant --
    /// but it forgives a host that never moves, which is why every frame claiming packet zero
    /// went unnoticed until it was read for.</summary>
    [Test]
    public void EveryF2PacketAdvancesTheSdpcmSequence()
    {
        using var pico = PicoWSimulation.Create(_firmware);
        pico.OfferAp("RP2350Sharp-AP", password: "hunter2");

        var seqs = new List<int>();
        string? joined = null;
        pico.Radio.OnWriteDebug += (fn, addr, host) =>
        {
            // F2 is the WLAN data function: an SDPCM frame follows the 4-byte command word,
            // so byte 4 of its header is host[8].
            if (fn == 2 && host.Length >= 16) seqs.Add(host[8]);
        };
        pico.Wifi.OnStaJoin += s => joined = s;

        for (int i = 0; i < 400_000 && joined == null; i++) pico.Step();
        for (int i = 0; i < 40_000; i++) pico.Step();

        seqs.Should().NotBeEmpty("the join sends SDPCM frames over F2");
        seqs.Distinct().Should().HaveCountGreaterThan(1,
            "the sequence must advance; every frame carrying the same number is the defect this "
            + $"pins. Saw: {string.Join(",", seqs)}");
        seqs.Should().BeEquivalentTo(Enumerable.Range(seqs[0], seqs.Count).Select(v => v & 0xFF),
            options => options.WithStrictOrdering(),
            "consecutive frames carry consecutive sequence numbers");
    }
}
