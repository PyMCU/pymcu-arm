using System.Collections.Concurrent;
using System.Diagnostics;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Compiles PyMCU RP2040 firmware using the <c>pymcu build</c> CLI driver.
/// Returns the flat flash binary for PicoSimulation.LoadFlash.
/// Results are cached in-process so each program is compiled at most once per
/// test session regardless of how many test fixtures reference it.
/// </summary>
public static class PymcuCompiler
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PymcuExe = Path.Combine(RepoRoot, ".venv", "bin", "pymcu");

    private static readonly SemaphoreSlim BuildGate = new(Math.Clamp(Environment.ProcessorCount, 2, 8));

    private static readonly ConcurrentDictionary<string, Lazy<byte[]>> BinCache = new();

    /// <summary>
    /// Compiles the RP2040 example at <c>examples/{name}</c> and returns
    /// the flat flash image (<c>dist/firmware.bin</c>) for PicoSimulation.LoadFlash.
    /// </summary>
    public static byte[] BuildRp2040(string name)
        => BinCache.GetOrAdd("rp:ex:" + name,
            _ => new Lazy<byte[]>(() => CompileBin(Path.Combine(RepoRoot, "examples", name), name))).Value;

    /// <summary>
    /// Compiles the RP2040 fixture at <c>tests/integration/fixtures/{name}</c>.
    /// </summary>
    public static byte[] BuildFixtureRp2040(string name)
        => BinCache.GetOrAdd("rp:fx:" + name,
            _ => new Lazy<byte[]>(() => CompileBin(Path.Combine(RepoRoot, "tests", "integration", "fixtures", name), name))).Value;

    /// <summary>
    /// Compiles the RP2350 example at <c>examples/{name}</c> and returns the flat
    /// flash image (<c>dist/firmware.bin</c>) for RP2350TestSimulation.WithBinary.
    /// </summary>
    public static byte[] BuildRp2350(string name)
        => BinCache.GetOrAdd("rp2350:ex:" + name,
            _ => new Lazy<byte[]>(() => CompileBin(Path.Combine(RepoRoot, "examples", name), name))).Value;

    /// <summary>
    /// Absolute path of an example directory — for tests that inspect build
    /// artifacts (e.g. <c>dist/debug/firmware.opt.ll</c>) after a Build* call.
    /// </summary>
    public static string ExampleDir(string name)
        => Path.Combine(RepoRoot, "examples", name);

    /// <summary>
    /// Compiles a generated RP2040 program given as source text, for corpora that are
    /// produced in-process rather than checked in. The program is materialized into a
    /// throwaway project under the system temp directory and built with <c>pymcu build</c>.
    /// Cached by content hash so identical programs compile once.
    /// </summary>
    public static byte[] BuildSourceRp2040(string mainPy)
        => BinCache.GetOrAdd("rp:src:" + Sha(mainPy), _ => new Lazy<byte[]>(() => CompileSource(mainPy))).Value;

    /// <summary>
    /// Directory of the throwaway project <see cref="BuildSourceRp2040"/> builds, for tests
    /// that need its artifacts (e.g. <c>dist/debug/firmware.opt.ll</c>).
    /// </summary>
    public static string SourceDir(string mainPy)
        => Path.Combine(Path.GetTempPath(), "pymcu-arm-gen", Sha(mainPy)[..16]);

    private static string Sha(string s)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private static byte[] CompileSource(string mainPy)
    {
        var dir = SourceDir(mainPy);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
            "[project]\n" +
            "name = \"gen\"\n" +
            "version = \"0.1.0\"\n\n" +
            "[tool.pymcu]\n" +
            "target = \"rp2040\"\n" +
            "frequency = 125000000\n" +
            "sources = \"src\"\n" +
            "entry = \"main.py\"\n");
        File.WriteAllText(Path.Combine(dir, "src", "main.py"), mainPy);
        return CompileBin(dir, "gen-" + Sha(mainPy)[..8]);
    }

    private static byte[] CompileBin(string projectDir, string name)
    {
        BuildGate.Wait();
        try { RunPymcuBuild(projectDir, name); }
        finally { BuildGate.Release(); }
        var binFile = Path.Combine(projectDir, "dist", "firmware.bin");
        if (!File.Exists(binFile))
            throw new FileNotFoundException($"Firmware bin not found after build: {binFile}");
        return File.ReadAllBytes(binFile);
    }

    private static readonly bool Verbose =
        Environment.GetEnvironmentVariable("PYMCU_VERBOSE") == "1" ||
        Environment.GetEnvironmentVariable("RUNNER_DEBUG")  == "1";

    private static void RunPymcuBuild(string projectDir, string name)
    {
        if (!Directory.Exists(projectDir))
            throw new DirectoryNotFoundException($"Project directory not found: {projectDir}");

        var venvBin = Path.Combine(RepoRoot, ".venv", "bin");
        var venvPython = Path.Combine(venvBin, "python3");
        var psi = new ProcessStartInfo
        {
            FileName = venvPython,
            Arguments = $"{PymcuExe} build",
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Verbose)
            psi.Environment["PYMCU_VERBOSE"] = "1";
        psi.Environment["PATH"] = venvBin + Path.PathSeparator + psi.Environment["PATH"];

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pymcu process.");
        var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
        var finished = proc.WaitForExit(120_000);
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        var failed = !finished || proc.ExitCode != 0;
        if (failed || Verbose)
        {
            if (failed)
            {
                Console.WriteLine($"[PymcuCompiler] Build failed: {name}");
                Console.WriteLine($"[PymcuCompiler] RepoRoot   : {RepoRoot}");
                Console.WriteLine($"[PymcuCompiler] ProjectDir : {projectDir}");
                Console.WriteLine($"[PymcuCompiler] PATH       : {psi.Environment["PATH"]}");
            }
            Console.WriteLine($"[PymcuCompiler] Exit: {(finished ? proc.ExitCode.ToString() : "TIMEOUT")}");
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine($"stdout:\n{stdout}");
            if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine($"stderr:\n{stderr}");
        }

        if (!finished) { proc.Kill(); throw new TimeoutException($"pymcu build timed out for '{name}'.\n{stdout}\n{stderr}"); }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"pymcu build failed for '{name}' (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "hatch_build.py")) &&
                Directory.Exists(Path.Combine(dir, "examples")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException(
            "Cannot locate pymcu-arm repo root (no hatch_build.py + examples/ found).");
    }
}
