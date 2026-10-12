"""Runs the pymcu-avr language oracle corpus (tests/oracle/probes/*.py) against
the ARM targets (rp2040, rp2350) this repo builds for, using RP2040Sharp/RP2350Sharp as the
reference emulator the same way pymcu-avr uses avr8sharp. MEASUREMENT ONLY: this does not
modify the shared corpus (read from the pymcu-avr checkout, untouched) and does not fix any
compiler behaviour it finds.

Three adaptations are made, all mechanical and applied uniformly to every probe:

1. The CPython reference run's chip shim is set to the real target chip/arch instead of the
   hardcoded atmega328p/avr pymcu-avr's own test_oracle.py uses -- needed so a probe that
   branches on `__CHIP__.name`/`.arch` (e.g. 175_chip_conditional.py) is compared fairly.
2. 155/440 probes seed a value from `pymcu.chips.atmega328p.GPIOR0`, an AVR-only spare
   register (`ptr(0x3E)`, boots to 0, read-only in this whole corpus -- ORACLE_SEED is never
   set to anything but 0 anywhere in pymcu-avr). Compiling as-is for rp2040/rp2350 fails
   (`pymcu.chips.atmega328p` does not exist off-AVR). The import line is swapped for an
   equivalent inline register at a scratch RAM address near the top of the target chip's
   SRAM (same ptr[uint8] shape the real chip register tables use) -- a real, zero-initialized,
   read/write byte, not a folded constant. This ONLY changes the firmware build's source, not
   the CPython reference (whose _Ptr shim is address-content-agnostic).
3. A handful of probes (196, 277; 278 shares the line but never runs it -- it is refused for
   an unrelated reason before the pointer is read) declare a `BASE` constant that assumes the
   ATmega328P's low SRAM map (0x0400, or 0x04 used as a page number via `BASE * 256` ==
   0x0400) and build a `ptr()` from it. 0x0400 is inside RP2040/RP2350's BOOTROM, not SRAM --
   on real AVR hardware this is free RAM, so the probe's own logic is sound; only the literal
   address is AVR-specific. BASE's declaration line is swapped for one that resolves to a
   scratch address near the top of the target chip's SRAM (the same region GPIOR0's
   replacement lives next to, offset so the two never overlap), widened to `uint32` --
   `uint8`/`uint16` cannot HOLD a 32-bit SRAM address, which is exactly why this could not be
   a same-width literal swap like GPIOR0's.

4. One probe (461) imports the WHOLE chip module (`import pymcu.chips.atmega328p as chip`)
   rather than a single register, to exercise `getattr(module, name, default)` -- PyMCU's one
   compile-time form of getattr, which only folds when the first argument is a real module
   import. The import is swapped for the equivalent import of the TARGET chip's own module
   (`import pymcu.chips.rp2040 as chip`, a module that genuinely exists for this target), same
   alias. run_cpython_for_target shims that module into sys.modules too (a GPIOR0 attribute
   that reads 0, the same shape install_cpython_shims already gives atmega328p's), so the
   CPython side resolves the SAME name through the SAME kind of object.
5. One probe (583) imports TWO registers on one line (`from pymcu.chips.atmega328p import
   GPIOR0, GPIOR1`) -- the single-name substring swap in adaptation 2 only replaced the
   "GPIOR0" part, leaving ", GPIOR1" dangling as a syntax error that masked the probe's own
   refusal (a `divmod` quotient that can exceed int32) behind an unrelated one. Generalized to
   any comma-separated list of register names on that import line, each becoming its own
   scratch byte (GPIOR1 one byte above GPIOR0's, so the two never alias).

Everything else about a probe -- its logic, its expected divergences, its `# tracked:` bugs --
is measured as found. A probe whose address literal isn't covered by one of the above (there
are none left in the corpus as of this writing, but a future one would hit this) is still
compiled and run unmodified; whatever happens (compile-fail, hard fault, wrong answer) is
itself the data point.

Usage: .venv/bin/python tests/oracle/sweep.py --chip rp2040 --out /path/out.jsonl [--limit N] [--only NAME]
       [--oracle-root DIR] [--pymcu PATH]

The corpus comes from --oracle-root, else $PYMCU_AVR_ORACLE_ROOT, else ../pymcu-avr; the
driver from --pymcu, else the pymcu beside the running interpreter, else PATH.
"""
from __future__ import annotations

import argparse
import contextlib
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import types
from pathlib import Path

ORACLE_ROOT_ENV = "PYMCU_AVR_ORACLE_ROOT"

REPO_ROOT = Path(__file__).resolve().parents[2]  # this repo's checkout (pymcu-arm)
RUNNER_DLL = REPO_ROOT / "build" / "oracle-runner" / "PyMCU.OracleRunner.ARM.dll"

# The corpus and the avr8sharp shim live in a pymcu-avr checkout. Set by main(), from
# --oracle-root, PYMCU_AVR_ORACLE_ROOT, or the pymcu-avr sibling of this repo.
AVR_ORACLE_ROOT: Path | None = None
PROBES_DIR: Path | None = None
TEST_ORACLE_FILE: Path | None = None
avr_oracle = None


def resolve_oracle_root(explicit: str | None) -> Path:
    """The pymcu-avr checkout whose tests/oracle/ holds the probes and the avr8sharp shim.

    --oracle-root wins, then PYMCU_AVR_ORACLE_ROOT, then ../pymcu-avr beside this repo.
    The script does not guess a checkout beyond that: a missing corpus is an error.
    """
    if explicit or os.environ.get(ORACLE_ROOT_ENV):
        raw = explicit or os.environ[ORACLE_ROOT_ENV]
        root, source = Path(raw).expanduser(), ("--oracle-root" if explicit else ORACLE_ROOT_ENV)
    else:
        root, source = REPO_ROOT.parent / "pymcu-avr", "the pymcu-avr sibling of this repo"
    if not (root / "tests" / "oracle" / "test_oracle.py").is_file():
        raise SystemExit(f"no pymcu-avr oracle corpus at {root} (from {source}); "
                         f"pass --oracle-root DIR or set {ORACLE_ROOT_ENV}")
    return root.resolve()


def resolve_pymcu(explicit: str | None) -> Path:
    """The pymcu driver to build the probes with.

    --pymcu wins. Otherwise the pymcu beside the interpreter running this script (the
    active venv), then pymcu on PATH. Nothing else is searched.
    """
    if explicit:
        pymcu = Path(explicit).expanduser()
        if not pymcu.is_file():
            raise SystemExit(f"--pymcu names {pymcu}, which is not a file")
        return pymcu
    beside = Path(sys.executable).parent / "pymcu"
    if beside.is_file():
        return beside
    on_path = shutil.which("pymcu")
    if on_path:
        return Path(on_path)
    raise SystemExit("no pymcu driver: pass --pymcu PATH, run this from a venv that has "
                     "pymcu-compiler installed, or put pymcu on PATH")

# Matches both the single-name form ("import GPIOR0") and 583's two-name one
# ("import GPIOR0, GPIOR1"), or any other comma-separated list of register names on the
# same import line -- group(1) is the raw name list, split and stripped below.
GPIOR_IMPORT_RE = re.compile(
    r"from pymcu\.chips\.atmega328p import ([A-Za-z0-9_, ]+)")

# A probe that imports the WHOLE chip module instead of one register (461), to call
# getattr(chip, name, default) -- PyMCU's one compile-time getattr form, which only folds
# for a real module import, not a class. group(1) is the import alias.
CHIP_MODULE_IMPORT_RE = re.compile(
    r"import pymcu\.chips\.atmega328p as (\w+)")

# Scratch byte near the top of each chip's SRAM, standing in for the AVR's GPIOR0 spare
# register. Same ptr[uint8] shape as every real per-chip register table in lib/src/pymcu/chips.
# A probe naming more than one register (583's GPIOR0, GPIOR1) gets one scratch byte per
# name, immediately above this one -- SCRATCH_ADDR + 1, + 2, ... -- so they never alias.
SCRATCH_ADDR = {
    "rp2040": 0x20041FF0,   # RAM 0x20000000 + 264 KiB, 16 bytes below top
    "rp2350": 0x2007FFF0,   # RAM 0x20000000 + 512 KiB, 16 bytes below top
}

# 196's `BASE: const[uint16] = 0x0400` (free SRAM on the ATmega328P, used as a direct
# 16-bit pointer base) and 277/278's `BASE: const[uint8] = 0x04` (the same address, written
# as a page number: `BASE * 256` == 0x0400) both assume AVR's low SRAM map. On RP2040/RP2350
# 0x0400 is inside BOOTROM. These sit further below SCRATCH_ADDR's byte -- enough room for
# 196's `ptr(BASE + 256)` and 277's two-register block (`BASE*256`, `BASE*256 + 1`) -- so
# neither collides with GPIOR0's replacement or each other. Widened to uint32: the whole
# reason this needs code, not a same-width literal swap, is that uint8/uint16 cannot hold a
# 32-bit SRAM address, the way GPIOR0's ptr[uint8] holds AVR's 16-bit one.
BASE196_ADDR = {
    "rp2040": 0x20041D00,
    "rp2350": 0x2007FD00,
}
# Stored pre-divided by 256: the probes' own source multiplies BASE back by 256
# (`BASE * 256`), so BASE itself must already be address // 256.
BASE277_PAGE = {
    "rp2040": 0x20041E00 // 256,
    "rp2350": 0x2007FE00 // 256,
}

# (pymcu chip name, arch, the frequency the driver builds the target at by default). The
# frequency is measured, not assumed: a program printing __FREQ__ built for rp2040 prints
# 125000000 and for rp2350 150000000. The CPython shim reports it, so a probe that branches
# on __FREQ__ compares against the clock the firmware actually runs at.
TARGET_CHIP_INFO = {
    "rp2040": ("rp2040", "arm", 125_000_000),
    "rp2350": ("rp2350", "arm", 150_000_000),
}

# A `# expect: refuse <text>` header is worded for AVR's toolchain (binutils' as/ld), which
# this sweep's own refusal may say correctly but differently: `@extern` on a symbol nothing
# defines refuses the SAME way on both -- a link-time undefined reference -- but AVR's ld
# phrases it "undefined reference to X" where LLVM's opt/llc/ld.lld (081 hits this from opt,
# verifying the IR before llc or ld.lld ever run) say "use of undefined value". One linker's
# wording is not a bug in the other; only the text this corpus asks for is AVR's. Probe 081
# is the one case of this in the corpus today.
LINKER_DIAGNOSTIC_EQUIVALENTS = {
    "undefined reference": "use of undefined value",
}


# Probes whose point is a property of the AVR chip the corpus was written for, not of the
# language: running them on another chip measures the chip. Reported as not measured, with the
# reason, instead of as a mismatch nobody can act on.
TARGET_SPECIFIC_PROBES = {
    # Sizes a module-level array to leave an ATmega328P's 2048-byte SRAM with almost no heap and
    # expects the next allocation to fail. An RP2040 has 264 KiB and a heap that takes everything
    # free below the stack, so the same array leaves it nearly all of it; the program succeeds,
    # which is correct there.
    "841_heap_exhausted_by_static_storage.py":
        "sized to exhaust an ATmega328P's 2 KiB of SRAM; the heap on this chip is two orders of "
        "magnitude larger, so there is no exhaustion to report",
}


def _diagnostic_matches(expected: str, log: str) -> bool:
    # The driver's own CLI pretty-prints diagnostics wrapped to a column width that
    # depends on the terminal/pipe it thinks it is writing to, which can -- and for 081,
    # does -- insert a real newline INSIDE the diagnostic text itself (observed: "use of
    # undefined" / "value '@...'" split across two lines once the temp project directory
    # name was long enough to shift the wrap point). Collapsing all whitespace runs to a
    # single space before comparing makes the match robust to wherever that wrap landed.
    normalized_log = " ".join(log.split())
    if " ".join(expected.split()) in normalized_log:
        return True
    equivalent = LINKER_DIAGNOSTIC_EQUIVALENTS.get(expected)
    return equivalent is not None and " ".join(equivalent.split()) in normalized_log


def _load_avr_oracle_module():
    spec = importlib.util.spec_from_file_location("avr_test_oracle", TEST_ORACLE_FILE)
    module = importlib.util.module_from_spec(spec)
    sys.modules["avr_test_oracle"] = module
    sys.path.insert(0, str(TEST_ORACLE_FILE.parent))
    try:
        spec.loader.exec_module(module)
    finally:
        sys.path.pop(0)
    return module


def probe_files() -> list[Path]:
    return sorted(PROBES_DIR.glob("*.py"))


def adapt_source(src: str, chip: str) -> str:
    # One line in, one line out for every substitution below: several "refuse" probes
    # carry a `# expect: refuse :LINE:COL: ...` header anchored to the ORIGINAL file's
    # line numbers, so a replacement that changes the line count would shift every
    # diagnostic below it and turn a real match into a false "wrong diagnostic".
    def gpior_sub(m: re.Match) -> str:
        names = [n.strip() for n in m.group(1).split(",") if n.strip()]
        decls = "; ".join(
            f"{name}: ptr[uint8] = ptr({hex(SCRATCH_ADDR[chip] + i)})"
            for i, name in enumerate(names)
        )
        return f"from pymcu.types import ptr, uint8; {decls}"

    src = GPIOR_IMPORT_RE.sub(gpior_sub, src)

    # 461: `import pymcu.chips.atmega328p as X` -> the equivalent import of the real
    # target chip module, same alias. run_cpython_for_target shims that module in too.
    src = CHIP_MODULE_IMPORT_RE.sub(rf"import pymcu.chips.{chip} as \1", src)

    # 196: BASE is a direct 16-bit pointer base (`ptr(BASE + off)`), so the replacement
    # just needs a wider type and a real address -- the arithmetic around it is untouched.
    # Neither probe imports uint32 (they only need the AVR-sized uint8/uint16 originally),
    # so the import rides the same line, same "GPIOR0 import" trick as above.
    base196 = "BASE: const[uint16] = 0x0400"
    if base196 in src:
        src = src.replace(
            base196,
            f"from pymcu.types import uint32; "
            f"BASE: const[uint32] = {hex(BASE196_ADDR[chip])}",
        )

    # 277/278: BASE is a page number the probe's own body multiplies by 256
    # (`ptr(BASE * 256)`), so the replacement stores the target address PRE-DIVIDED by
    # 256 rather than the address itself -- the body's `* 256` does the rest.
    base277 = "BASE: const[uint8] = 0x04"
    if base277 in src:
        src = src.replace(
            base277,
            f"from pymcu.types import uint32; "
            f"BASE: const[uint32] = {hex(BASE277_PAGE[chip])}",
        )

    return src


def run_cpython_for_target(src: str, probe_name: str, chip_name: str, arch: str, freq: int) -> str:
    """Same as avr_oracle.run_cpython, but the chip shim reports the real target chip
    instead of the hardcoded atmega328p/avr -- see module docstring, adaptation 1."""
    buf = io.StringIO()
    previous = avr_oracle.install_cpython_shims()
    chip_mod = sys.modules["pymcu.chips.atmega328p"]
    chip_mod.__CHIP__.name = chip_name
    chip_mod.__CHIP__.arch = arch
    sys.modules["pymcu.chips"].__CHIP__ = chip_mod.__CHIP__
    # The shim installs the AVR clock (16 MHz); the target runs at its own.
    sys.modules["pymcu"].__FREQ__ = freq
    sys.modules["pymcu.chips"].__FREQ__ = freq

    # Adaptation 4 (461): a fake pymcu.chips.<target> module, GPIOR0 shimmed the same shape
    # as atmega328p's own (install_cpython_shims above), so `import pymcu.chips.rp2040 as
    # chip; getattr(chip, "GPIOR0", None)` resolves to the SAME kind of object the firmware
    # build's adapted source reads through its scratch ptr.
    target_chip_mod = types.ModuleType(f"pymcu.chips.{chip_name}")
    target_chip_mod.GPIOR0 = types.SimpleNamespace(value=chip_mod.GPIOR0.value)
    sys.modules[f"pymcu.chips.{chip_name}"] = target_chip_mod

    globals_ = {"__name__": "__main__", "__file__": str(PROBES_DIR / f"{probe_name}.py")}
    # The harness writes the program as main.py in the project, and a probe may open it by
    # name (684: open(name) with name = "main.py"). The reference runs in a directory that
    # has that file too, so the probe sees what it sees on the target.
    try:
        with tempfile.TemporaryDirectory(prefix="oracle-cpython-") as cwd:
            Path(cwd, "main.py").write_text(src)
            with contextlib.redirect_stdout(buf), contextlib.chdir(cwd):
                try:
                    exec(compile(src, probe_name, "exec"), globals_)
                except SystemExit:
                    pass
                except BaseException as exc:  # noqa: BLE001 - mirrors avr_oracle.run_cpython
                    msg = str(exc)
                    print(f"E:{type(exc).__name__}: {msg}" if msg else f"E:{type(exc).__name__}")
    finally:
        avr_oracle.restore_modules(previous)
    return buf.getvalue()


def compile_probe(tmp_dir: Path, name: str, src: str, chip: str, pymcu: Path) -> tuple[int, str | None, str]:
    project = tmp_dir / name
    src_dir = project / "src"
    src_dir.mkdir(parents=True)
    (src_dir / "main.py").write_text(src)
    (project / "pyproject.toml").write_text(
        "[project]\n"
        'name = "oracle-probe"\n'
        'version = "0.1.0"\n'
        'requires-python = ">=3.11"\n\n'
        "[tool.pymcu]\n"
        f'target = "{chip}"\n'
        'sources = "src"\n'
        'entry = "main.py"\n'
    )
    result = subprocess.run(
        [str(pymcu), "build"], cwd=project, capture_output=True, text=True, timeout=90,
    )
    bin_path = project / "dist" / "firmware.bin"
    return result.returncode, (str(bin_path) if bin_path.is_file() else None), result.stdout + result.stderr


def run_emulator(chip: str, firmware_bin: str, max_ms: float = 4000) -> tuple[str, bool]:
    """The console output, and whether the core ended locked up.

    The runner prints "[runner] <chip> hardfaults=... lockup=True|False" on stderr for both
    chips. A HardFault escalates to lockup on these cores, so lockup is the crash signal;
    rp2040's TestKit has no HardFault counter to report.
    """
    result = subprocess.run(
        ["dotnet", str(RUNNER_DLL), chip, firmware_bin, str(max_ms)],
        capture_output=True, text=True, timeout=90,
    )
    lockup = re.search(r"lockup=(True|False)", result.stderr)
    return result.stdout.replace("\r\n", "\n"), bool(lockup and lockup.group(1) == "True")


def evaluate(probe: Path, chip: str, tmp_dir: Path, pymcu: Path) -> dict:
    src = probe.read_text()
    expectation = avr_oracle.parse_expectation(src)
    name = probe.stem
    record = {"probe": probe.name, "expect": expectation.kind, "chip": chip}
    if expectation.tracked:
        record["tracked"] = expectation.tracked

    if probe.name in TARGET_SPECIFIC_PROBES:
        record["outcome"] = "no_medido"
        record["reason"] = TARGET_SPECIFIC_PROBES[probe.name]
        return record

    if expectation.frontend is not None and expectation.frontend != "default":
        record["outcome"] = "no_medido"
        record["reason"] = f"restricted to the {expectation.frontend} front end; this sweep runs the default front end only"
        return record

    chip_name, arch, freq = TARGET_CHIP_INFO[chip]
    adapted = adapt_source(src, chip)
    returncode, bin_path, log = compile_probe(tmp_dir, name, adapted, chip, pymcu)

    if expectation.kind == "refuse":
        if returncode != 0:
            if expectation.diagnostic and not _diagnostic_matches(expectation.diagnostic, log):
                record["outcome"] = "refused_wrong_diagnostic"
                record["detail"] = log.strip().splitlines()[-1][:200] if log.strip() else ""
            else:
                record["outcome"] = "refused"
            return record
        record["outcome"] = "unexpected_compile_success"
        return record

    if returncode != 0 or bin_path is None:
        interesting = [
            line for line in log.splitlines()
            if any(k in line.lower() for k in ("error", "exception", "unsupported", "refus"))
        ]
        detail = (interesting or log.splitlines() or [""])[-1][:200]
        record["outcome"] = "compile_fail"
        record["detail"] = detail
        return record

    if expectation.kind == "compile":
        # `expect: compile` probes only have to build. They print nothing and never reach
        # END, so running them would read an empty console as a crash.
        record["outcome"] = "compiled"
        return record

    try:
        actual, locked_up = run_emulator(chip, bin_path)
    except subprocess.TimeoutExpired:
        record["outcome"] = "runner_timeout"
        return record

    expected = run_cpython_for_target(src, name, chip_name, arch, freq)
    if expectation.kind == "divergence":
        expected = avr_oracle.apply_divergence(expectation.divergence_doc, expected)

    if locked_up:
        # The core reached lockup: a HardFault escalated, or the firmware crashed.
        record["outcome"] = "crash"
        record["detail"] = actual[-200:]
    elif expected == actual:
        record["outcome"] = "match"
    elif actual.strip() == "" and "END\n" not in actual:
        # Silence and no lockup: the run never printed anything, so it never reached END
        # within the budget. A loop, or code that never returns.
        record["outcome"] = "timeout"
    else:
        record["outcome"] = "mismatch"
        record["detail"] = avr_oracle.first_difference(expected, actual)
        record["expected_tail"] = expected[-200:]
        record["actual_tail"] = actual[-200:]
    return record


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--chip", required=True, choices=["rp2040", "rp2350"])
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=None)
    ap.add_argument("--only", default=None, help="substring filter on probe filename")
    ap.add_argument("--oracle-root", default=None,
                    help=f"pymcu-avr checkout with tests/oracle/ (default: ${ORACLE_ROOT_ENV}, "
                         "else ../pymcu-avr beside this repo)")
    ap.add_argument("--pymcu", default=None,
                    help="pymcu driver (default: the one beside this interpreter, else on PATH)")
    args = ap.parse_args()

    global AVR_ORACLE_ROOT, PROBES_DIR, TEST_ORACLE_FILE, avr_oracle
    AVR_ORACLE_ROOT = resolve_oracle_root(args.oracle_root)
    PROBES_DIR = AVR_ORACLE_ROOT / "tests" / "oracle" / "probes"
    TEST_ORACLE_FILE = AVR_ORACLE_ROOT / "tests" / "oracle" / "test_oracle.py"
    avr_oracle = _load_avr_oracle_module()
    pymcu = resolve_pymcu(args.pymcu)
    assert RUNNER_DLL.is_file(), f"runner not built: {RUNNER_DLL}"

    probes = probe_files()
    if args.only:
        probes = [p for p in probes if args.only in p.name]
    if args.limit:
        probes = probes[: args.limit]

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    with out_path.open("w") as fh:
        for i, probe in enumerate(probes, 1):
            with tempfile.TemporaryDirectory() as tmp:
                try:
                    record = evaluate(probe, args.chip, Path(tmp), pymcu)
                except Exception as exc:  # noqa: BLE001 - keep sweeping past a bad probe
                    record = {"probe": probe.name, "chip": args.chip, "outcome": "sweep_error",
                              "detail": repr(exc)}
            fh.write(json.dumps(record) + "\n")
            fh.flush()
            if i % 25 == 0 or i == len(probes):
                elapsed = time.time() - t0
                print(f"[{args.chip}] {i}/{len(probes)} ({elapsed:.0f}s)", file=sys.stderr)


if __name__ == "__main__":
    main()
