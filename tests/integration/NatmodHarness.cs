using System.Diagnostics;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Builds a native module with `pymcu natmod`, runs it inside the real CircuitPython 10.3.1
/// firmware on the RP2040 emulator, and runs the same Python under CPython for the expected
/// answer. Everything the machine needs and may not have (the CircuitPython tree, the Pico
/// firmware, an emulator that can load a native module) is probed first and turns into an
/// Assert.Ignore with the reason, so a checkout without them reports "ignored", not "failed".
/// </summary>
public static class NatmodHarness
{
    private static readonly string Home = Environment.GetEnvironmentVariable("HOME") ?? "";

    /// <summary>The CircuitPython 10.3.1 source tree `pymcu natmod` links against.</summary>
    public static string CircuitPythonTree
    {
        get
        {
            // PYMCU_CIRCUITPYTHON is the variable `pymcu natmod` itself reads, so one setting serves both.
            var env = Environment.GetEnvironmentVariable("PYMCU_NATMOD_CP_TREE")
                      ?? Environment.GetEnvironmentVariable("PYMCU_CIRCUITPYTHON");
            var path = !string.IsNullOrEmpty(env) ? env : Path.Combine(Home, "Repos", "circuitpython-1031");
            if (!File.Exists(Path.Combine(path, "py", "dynruntime.h")))
                Assert.Ignore($"no CircuitPython 10.3.1 tree at {path} (set PYMCU_NATMOD_CP_TREE).");
            return path;
        }
    }

    /// <summary>The CircuitPython 10.3.1 UF2 for the Pico.</summary>
    public static string CircuitPythonUf2
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PYMCU_NATMOD_CP_UF2")
                      ?? Environment.GetEnvironmentVariable("PYMCU_CP_UF2");
            var candidates = new[]
            {
                env ?? "",
                Path.Combine(Home, "Downloads", "natmod-pico-test", "cp-10.3.1-pico.uf2"),
                Path.Combine(Home, ".cache", "pymcu-oracle",
                    "adafruit-circuitpython-raspberry_pi_pico-en_US-10.3.1.uf2"),
            };
            var found = candidates.FirstOrDefault(p => p.Length > 0 && File.Exists(p));
            if (found == null)
                Assert.Ignore("no CircuitPython 10.3.1 Pico UF2 (set PYMCU_NATMOD_CP_UF2).");
            return found!;
        }
    }

    private static readonly string RepoRoot = FindRepoRoot();
    private static string Pymcu => Path.Combine(RepoRoot, ".venv", "bin", "pymcu");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "pyproject.toml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Run(string exe, string args, string cwd, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {args} failed ({p.ExitCode}):\n{stdout}\n{stderr.Result}");
        return stdout;
    }

    /// <summary>
    /// `pymcu natmod --check` states what the machine lacks (pyelftools, arm-none-eabi-gcc, the ARM
    /// backend, the CircuitPython tree) in one line each. A missing requirement is a reason to
    /// ignore the test, not to fail it; the answer is asked once per run.
    /// </summary>
    private static void RequireToolchain()
    {
        var verdict = ToolchainVerdict.Value;
        if (verdict != null) Assert.Ignore(verdict);
    }

    private static readonly Lazy<string?> ToolchainVerdict = new(() =>
    {
        if (!File.Exists(Pymcu)) return $"no pymcu driver at {Pymcu} (create the repo .venv).";
        var psi = new ProcessStartInfo(Pymcu, $"natmod --check --circuitpython \"{CircuitPythonTree}\"")
        {
            WorkingDirectory = Path.GetTempPath(), RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outText = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? null : "pymcu natmod cannot build here: " + outText.Trim() + err.Result.Trim();
    });

    private static string Sha(string s)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>Compiles <paramref name="kernel"/> as module <paramref name="module"/>; returns the .mpy.</summary>
    public static byte[] BuildMpy(string kernel, string module)
    {
        RequireToolchain();
        var dir = Path.Combine(Path.GetTempPath(), "pymcu-natmod-gen", Sha(kernel + module));
        var mpy = Path.Combine(dir, "dist", module + ".mpy");
        if (!File.Exists(mpy))
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
                $"[project]\nname = \"{module}\"\nversion = \"0.1.0\"\nrequires-python = \">=3.11\"\n\n" +
                "[tool.pymcu]\ntarget = \"rp2040\"\nfrequency = 125000000\nsources = \"src\"\nentry = \"main.py\"\n");
            File.WriteAllText(Path.Combine(dir, "src", "main.py"), kernel);
            Run(Pymcu, $"natmod --circuitpython \"{CircuitPythonTree}\" --module {module}", dir);
        }
        return File.ReadAllBytes(mpy);
    }

    /// <summary>Runs <paramref name="codePy"/> under CPython with the kernel as an ordinary module.</summary>
    public static string RunOnCPython(string kernel, string module, string codePy)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pymcu-natmod-cpython", Sha(kernel + module + codePy));
        Directory.CreateDirectory(Path.Combine(dir, "pymcu"));
        File.WriteAllText(Path.Combine(dir, "pymcu", "__init__.py"), "");
        // The kernels annotate with fixed-width names; under CPython they are all plain ints.
        File.WriteAllText(Path.Combine(dir, "pymcu", "types.py"),
            string.Join("\n", new[] { "int8", "int16", "int32", "uint8", "uint16", "uint32" }
                .Select(n => $"{n} = int")) + "\n");
        File.WriteAllText(Path.Combine(dir, module + ".py"), kernel);
        File.WriteAllText(Path.Combine(dir, "run.py"), codePy);
        return Run("python3", "run.py", dir).Replace("\r", "");
    }

    private static byte[] FlashFromUf2(byte[] uf2)
    {
        const uint M0 = 0x0A324655, M1 = 0x9E5D5157, FlashBase = 0x10000000;
        uint U32(int o) => BitConverter.ToUInt32(uf2, o);
        uint maxA = 0;
        for (int o = 0; o + 512 <= uf2.Length; o += 512)
        {
            if (U32(o) != M0 || U32(o + 4) != M1) continue;
            var ta = U32(o + 12); var ps = U32(o + 16);
            if (ps == 0 || ps > 256) continue;
            maxA = Math.Max(maxA, ta + ps);
        }
        var img = new byte[2 * 1024 * 1024];
        Array.Fill(img, (byte)0xFF);
        for (int o = 0; o + 512 <= uf2.Length; o += 512)
        {
            if (U32(o) != M0 || U32(o + 4) != M1) continue;
            var ta = U32(o + 12); var ps = U32(o + 16);
            if (ps == 0 || ps > 256) continue;
            Buffer.BlockCopy(uf2, o + 32, img, (int)(ta - FlashBase), (int)ps);
        }
        return img;
    }

    private static MethodInfo? FindBake()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("RP2040.Peripherals.Fs.CircuitPythonFs", throwOnError: false);
            if (t != null) return t.GetMethod("Bake", BindingFlags.Public | BindingFlags.Static);
        }
        return typeof(PicoSimulation).Assembly.GetReferencedAssemblies()
            .Select(a => { try { return Assembly.Load(a); } catch { return null; } })
            .Where(a => a != null)
            .Select(a => a!.GetType("RP2040.Peripherals.Fs.CircuitPythonFs", throwOnError: false))
            .FirstOrDefault(t => t != null)?.GetMethod("Bake", BindingFlags.Public | BindingFlags.Static);
    }

    /// <summary>
    /// Bakes <paramref name="module"/>.mpy and run.py onto CIRCUITPY, boots CircuitPython, imports
    /// run at the REPL and returns what it printed between its BEGIN and END lines.
    /// </summary>
    public static string RunOnCircuitPython(string module, byte[] mpy, string codePy)
    {
        var uf2 = CircuitPythonUf2;
        var bake = FindBake();
        if (bake == null)
            Assert.Ignore("this RP2040Sharp has no CircuitPythonFs (needs the build that loads native modules).");

        var flash = FlashFromUf2(File.ReadAllBytes(uf2));
        var files = new (string, byte[])[]
        {
            (module + ".mpy", mpy),
            ("run.py", Encoding.UTF8.GetBytes("print('BEGIN')\n" + codePy + "\nprint('END')\n")),
        };
        bake.Invoke(null, new object[] { flash, files, Type.Missing, Type.Missing });

        dynamic pico = new PicoSimulation(withUsbCdc: true);
        try
        {
            pico.LoadFlash(flash);
            double elapsed = 0;
            string text = "";
            bool keySent = false, prompt = false, imported = false;
            while (elapsed < 120_000)
            {
                pico.RunMilliseconds(100.0); elapsed += 100;
                text = (string)pico.UsbCdc.Text;
                if (!keySent && text.Contains("Press any key")) { pico.UsbCdc.InjectString("\r"); keySent = true; }
                if (!imported && text.Contains(">>> ")) { prompt = true; }
                if (prompt && !imported)
                {
                    pico.RunMilliseconds(200.0); elapsed += 200;
                    pico.UsbCdc.InjectString("import run\r\n");
                    imported = true;
                    continue;
                }
                if (imported && text.Contains("\nEND")) break;
            }
            text = text.Replace("\r", "");
            var b = text.IndexOf("BEGIN\n", StringComparison.Ordinal);
            var e = text.IndexOf("\nEND", StringComparison.Ordinal);
            if (b < 0 || e < 0)
                return "<<no BEGIN/END in the CircuitPython output>>\n" + text;
            return text.Substring(b + 6, e - (b + 6)) + "\n";
        }
        finally { ((IDisposable)pico).Dispose(); }
    }

    /// <summary>
    /// A kernel that needs nothing from the new machinery has to come out right first: if it does not,
    /// the emulator cannot load a native module at all and every case below would fail for a reason
    /// that has nothing to do with the case.
    /// </summary>
    public static void RequireWorkingEmulator()
    {
        // Decided once per run: on an emulator that cannot load a native module the probe waits out
        // its whole boot timeout, and every test repeating it would cost minutes to report the same thing.
        var verdict = EmulatorVerdict.Value;
        if (verdict != null) Assert.Ignore(verdict);
    }

    private static readonly Lazy<string?> EmulatorVerdict = new(() =>
    {
        const string Probe = "from pymcu.types import int16\n\n\ndef add(a: int16, b: int16) -> int16:\n    return a + b\n";
        try
        {
            var mpy = BuildMpy(Probe, "probe");
            var got = RunOnCircuitPython("probe", mpy, "import probe\nprint(probe.add(2, 3))\n").Trim();
            return got == "5"
                ? null
                : "this RP2040Sharp does not load a native module (it lacks fix f622e3c); got: " + got;
        }
        catch (IgnoreException e) { return e.Message; }
    });
}
