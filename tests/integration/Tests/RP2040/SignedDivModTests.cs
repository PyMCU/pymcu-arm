using System.Text;
using FluentAssertions;
using NUnit.Framework;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Extensions;

namespace PyMCU.IntegrationTests.Tests.RP2040;

[TestFixture]
public class SignedDivModTests
{
    [TestCase("int8", 1)]
    [TestCase("int16", 2)]
    [TestCase("int32", 4)]
    public void FloorDivMod_MatchesPythonSemantics(string typeName, int bytes)
    {
        var prog = SignedDivModProgram.Generate(typeName, bytes);
        var firmware = PymcuCompiler.BuildSourceRp2040(prog.Source);

        using var pico = new PicoSimulation(withUsbCdc: false);
        pico.LoadFlash(firmware);

        pico.RunUntilOutput(pico.Uart0, "GO", timeoutMs: 20_000)
            .Should().BeTrue("the firmware must reach its banner before the corpus runs");
        pico.Uart0.InjectByte(prog.InputByte);
        pico.RunUntilOutput(pico.Uart0,
            _ => CountNewlines(Text(pico)) >= prog.Expected.Count + 1,
            timeoutMs: 120_000);

        var got = ParseSignedAfterBanner(Text(pico), prog.Expected.Count);
        got.Should().Equal(prog.Expected,
            $"{typeName}: simulated signed //,% must match Python's floor semantics.\n" +
            $"--- uart ---\n{Text(pico)}");
    }

    private static string Text(PicoSimulation pico)
    {
        var sb = new StringBuilder();
        foreach (var b in pico.Uart0.Bytes) sb.Append((char)b);
        return sb.ToString();
    }

    private static int CountNewlines(string s)
    {
        int n = 0;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }

    private static List<long> ParseSignedAfterBanner(string text, int count)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim() == "GO");
        var result = new List<long>();
        for (int i = start + 1; i < lines.Length && result.Count < count; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (long.TryParse(t, out long v)) result.Add(v);
            else break;
        }
        return result;
    }
}
