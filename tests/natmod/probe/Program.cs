// probe <cp.uf2> <module.mpy> <module> <expr>...
// Bakes the .mpy onto CIRCUITPY inside a CircuitPython UF2, boots it in RP2040Sharp, imports
// the module and evaluates each expression at the REPL. One line per fact on stdout:
//   DIR <json list>      what dir(module) holds after the import
//   EXPR <json {expr, out}>   the REPL text that followed the expression
using System.Text.Json;
using RP2040.TestKit.Boards;
using RP2040.Peripherals.Fs;

var uf2 = File.ReadAllBytes(args[0]);
var mpy = File.ReadAllBytes(args[1]);
var mod = args[2];

const uint M0 = 0x0A324655, M1 = 0x9E5D5157;
uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
uint maxA = 0;
for (var i = 0; i + 512 <= uf2.Length; i += 512)
{
    if (U32(uf2, i) != M0 || U32(uf2, i + 4) != M1) continue;
    var ta = U32(uf2, i + 12); var ps = U32(uf2, i + 16);
    if (ps == 0 || ps > 256) continue;
    maxA = Math.Max(maxA, ta + ps);
}
var flash = new byte[Math.Max((int)(maxA - 0x10000000), 2 * 1024 * 1024)];
Array.Fill(flash, (byte)0xFF);
for (var i = 0; i + 512 <= uf2.Length; i += 512)
{
    if (U32(uf2, i) != M0 || U32(uf2, i + 4) != M1) continue;
    var ta = U32(uf2, i + 12); var ps = U32(uf2, i + 16);
    if (ps == 0 || ps > 256) continue;
    Buffer.BlockCopy(uf2, i + 32, flash, (int)(ta - 0x10000000), (int)ps);
}
CircuitPythonFs.Bake(flash, new (string, byte[])[] { (mod + ".mpy", mpy) });

var sim = new PicoSimulation(withUsbCdc: true);
sim.LoadFlash(flash);

var ready = false;
for (double t = 0, keyAt = -1; t < 30_000 && !ready; t += 100)
{
    sim.RunMilliseconds(100);
    if (keyAt < 0 && sim.UsbCdc.Text.Contains("Press any key", StringComparison.OrdinalIgnoreCase))
    { sim.UsbCdc.InjectString("\r"); keyAt = t; }
    ready = sim.UsbCdc.Text.Contains(">>> ", StringComparison.Ordinal);
}
if (!ready) { Console.Error.WriteLine("no REPL prompt"); return 2; }
sim.RunMilliseconds(100);

string Run(string line, int ms)
{
    sim.UsbCdc.Clear();
    sim.UsbCdc.InjectString(line + "\r\n");
    sim.RunMilliseconds(ms);
    return sim.UsbCdc.Text;
}

Run("import " + mod, 1500);
Console.WriteLine("DIR " + JsonSerializer.Serialize(Run("print(dir(" + mod + "))", 1000)));
for (var k = 3; k < args.Length; k++)
    Console.WriteLine("EXPR " + JsonSerializer.Serialize(new { expr = args[k], @out = Run(args[k], 1200) }));
return 0;
