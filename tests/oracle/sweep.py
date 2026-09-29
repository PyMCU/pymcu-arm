"""Runs the pymcu-avr language oracle corpus (440 probes, tests/oracle/probes/*.py) against
the ARM targets (rp2040, rp2350) this repo builds for, using RP2040Sharp/RP2350Sharp as the
reference emulator the same way pymcu-avr uses avr8sharp. MEASUREMENT ONLY: this does not
modify the shared corpus (read from the pymcu-avr checkout, untouched) and does not fix any
compiler behaviour it finds.

Two adaptations are made, both mechanical and applied uniformly to every probe:

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

Everything else about a probe -- its logic, its expected divergences, its `# tracked:` bugs --
is measured as found. A probe that cannot be adapted this way (raw fixed-address `ptr()` bases
that assume the AVR's low SRAM map, e.g. 277/278) is still compiled and run unmodified; whatever
happens (compile-fail, hard fault, wrong answer) is itself the data point.

Usage: .venv/bin/python tests/oracle/sweep.py --chip rp2040 --out /path/out.jsonl [--limit N] [--only NAME]
"""
from __future__ import annotations

import argparse
import contextlib
import importlib.util
import io
import json
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

AVR_ORACLE_ROOT = Path.home() / "Repos" / "pymcu-avr"
PROBES_DIR = AVR_ORACLE_ROOT / "tests" / "oracle" / "probes"
TEST_ORACLE_FILE = AVR_ORACLE_ROOT / "tests" / "oracle" / "test_oracle.py"

REPO_ROOT = Path(__file__).resolve().parents[2]  # pymcu-arm-oracle worktree
RUNNER_DLL = REPO_ROOT / "build" / "oracle-runner" / "PyMCU.OracleRunner.ARM.dll"

GPIOR0_IMPORT = "from pymcu.chips.atmega328p import GPIOR0"

# Scratch byte near the top of each chip's SRAM, standing in for the AVR's GPIOR0 spare
# register. Same ptr[uint8] shape as every real per-chip register table in lib/src/pymcu/chips.
SCRATCH_ADDR = {
    "rp2040": 0x20041FF0,   # RAM 0x20000000 + 264 KiB, 16 bytes below top
    "rp2350": 0x2007FFF0,   # RAM 0x20000000 + 512 KiB, 16 bytes below top
}
TARGET_CHIP_INFO = {
    "rp2040": ("rp2040", "arm"),
    "rp2350": ("rp2350", "arm"),
}


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


avr_oracle = _load_avr_oracle_module()


def probe_files() -> list[Path]:
    return sorted(PROBES_DIR.glob("*.py"))


def adapt_source(src: str, chip: str) -> str:
    if GPIOR0_IMPORT not in src:
        return src
    # One line in, one line out: several "refuse" probes carry a `# expect: refuse
    # :LINE:COL: ...` header anchored to the ORIGINAL file's line numbers, so a
    # replacement that changes the line count would shift every diagnostic below it
    # and turn a real match into a false "wrong diagnostic".
    replacement = (
        f"from pymcu.types import ptr, uint8; GPIOR0: ptr[uint8] = ptr({hex(SCRATCH_ADDR[chip])})"
    )
    return src.replace(GPIOR0_IMPORT, replacement)


def run_cpython_for_target(src: str, probe_name: str, chip_name: str, arch: str) -> str:
    """Same as avr_oracle.run_cpython, but the chip shim reports the real target chip
    instead of the hardcoded atmega328p/avr -- see module docstring, adaptation 1."""
    buf = io.StringIO()
    previous = avr_oracle.install_cpython_shims()
    chip_mod = sys.modules["pymcu.chips.atmega328p"]
    chip_mod.__CHIP__.name = chip_name
    chip_mod.__CHIP__.arch = arch
    sys.modules["pymcu.chips"].__CHIP__ = chip_mod.__CHIP__
    globals_ = {"__name__": "__main__", "__file__": str(PROBES_DIR / f"{probe_name}.py")}
    try:
        with contextlib.redirect_stdout(buf):
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


def run_emulator(chip: str, firmware_bin: str, max_ms: float = 4000) -> str:
    result = subprocess.run(
        ["dotnet", str(RUNNER_DLL), chip, firmware_bin, str(max_ms)],
        capture_output=True, text=True, timeout=90,
    )
    return result.stdout.replace("\r\n", "\n")


def evaluate(probe: Path, chip: str, tmp_dir: Path, pymcu: Path) -> dict:
    src = probe.read_text()
    expectation = avr_oracle.parse_expectation(src)
    name = probe.stem
    record = {"probe": probe.name, "expect": expectation.kind, "chip": chip}
    if expectation.tracked:
        record["tracked"] = expectation.tracked

    if expectation.frontend is not None and expectation.frontend != "default":
        record["outcome"] = "no_medido"
        record["reason"] = f"restricted to the {expectation.frontend} front end; this sweep runs the default front end only"
        return record

    chip_name, arch = TARGET_CHIP_INFO[chip]
    adapted = adapt_source(src, chip)
    returncode, bin_path, log = compile_probe(tmp_dir, name, adapted, chip, pymcu)

    if expectation.kind == "refuse":
        if returncode != 0:
            if expectation.diagnostic and expectation.diagnostic not in log:
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

    try:
        actual = run_emulator(chip, bin_path)
    except subprocess.TimeoutExpired:
        record["outcome"] = "runner_timeout"
        return record

    expected = run_cpython_for_target(src, name, chip_name, arch)
    if expectation.kind == "divergence":
        expected = avr_oracle.apply_divergence(expectation.divergence_doc, expected)

    if expected == actual:
        record["outcome"] = "match"
    elif "END\n" not in actual and actual.strip() == "":
        record["outcome"] = "crash_or_timeout"
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
    ap.add_argument("--pymcu", default=str(Path.home() / "Repos/PyMCU-targets3/.venv/bin/pymcu"))
    args = ap.parse_args()

    pymcu = Path(args.pymcu)
    assert pymcu.is_file(), f"pymcu binary not found: {pymcu}"
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
