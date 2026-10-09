# -----------------------------------------------------------------------------
# PyMCU RP2040 LLVM Toolchain
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
Rp2040LlvmToolchain -- drives LLVM to turn the backend's LLVM IR (.ll) into a
flashable RP2040 flat-binary image.

Pipeline (assemble):
    opt  -O2                      firmware.ll  -> firmware.opt.ll
    llc  -mtriple=thumbv6m...      firmware.opt.ll -> firmware.o
    llvm-mc                        boot2.S / crt0.S -> *.o
    ld.lld -T rp2040.ld            *.o -> firmware.elf
    llvm-objcopy -O binary         firmware.elf -> firmware.bin   (boot2 at offset 0)
    uf2.write_uf2                  firmware.bin -> firmware.uf2   (BOOTSEL image)

LLVM binaries are resolved from (in order): the vendored toolchain wheel cache
under ~/.pymcu/tools, common system install dirs (e.g. Homebrew's keg), then
PATH. The runtime sources (boot2.S, crt0.S, rp2040.ld) ship with the extension.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from rich.console import Console
from pymcu.toolchain.sdk import ExternalToolchain

from .uf2 import write_uf2

# Per-chip LLVM target: (triple, cpu). RP2350 is compiled soft-float (float is
# unsupported in the backend anyway), so the same datalayout is valid for both.
_TARGETS = {
    "rp2040": ("thumbv6m-none-eabi", "cortex-m0plus"),
    "rp2350": ("thumbv8m.main-none-eabi", "cortex-m33"),
}

# Default for arch aliases / unknown chips: behave as RP2040 (historical default).
TARGET_TRIPLE = "thumbv6m-none-eabi"
TARGET_CPU = "cortex-m0plus"


def _resolve_target(chip: str) -> tuple[str, str]:
    """Map a chip id to its (triple, cpu); default to RP2040."""
    return _TARGETS.get((chip or "").lower(), (TARGET_TRIPLE, TARGET_CPU))

# Native-module (.mpy) architectures, keyed by chip:
#   (mpy arch name, llvm triple, llvm cpu, extra llc flags)
#
# The arch name is CircuitPython's own `--arch` spelling for tools/mpy_ld.py, and it
# goes in the .mpy header, so it is a PROMISE to every board of that architecture and
# not only to the one this project targets. The cpu is chosen to keep that promise,
# which is why it is NOT the chip's own core:
#
#   rp2350 -> cortex-m4, not cortex-m33. The loader identifies that port as armv7emsp
#     and mpy_ld.py has no armv8m entry at all, so the header has to say armv7emsp.
#     Measured: `-mtriple=thumbv7em -mcpu=cortex-m33` still stamps the object
#     CPU_arch = ARM v8-M Mainline, so the code was free to use v8m-only instructions
#     under a header promising v7E-M. An M33 would have run it and an M4 or M7 board
#     would not. cortex-m4 makes the object say what the header says.
#   rp2040 -> cortex-m0, not cortex-m0plus, for the same reason: armv6m in the header
#     covers M0 boards too.
#
# The float flags are py/dynruntime.mk's for that arch, so the kernel and the
# generated adapter agree about the float ABI. Nothing crosses the boundary as a float
# today, but the two objects call each other directly and a silent disagreement here
# would be found the day one does.
_NATMOD_ARCHES = {
    "rp2040": ("armv6m", "thumbv6m-none-eabi", "cortex-m0", []),
    "rp2350": ("armv7emsp", "thumbv7em-none-eabi", "cortex-m4", ["-float-abi=hard"]),
}

_REQUIRED_BINS = ["opt", "llc", "llvm-mc", "ld.lld", "llvm-objcopy"]

# Extra directories to probe for LLVM binaries when they are not on PATH.
# POSIX kegs first; Windows install locations are appended so a system LLVM
# (the official installer or winget) is found without requiring it on PATH.
_LLVM_SEARCH_DIRS = [
    "/opt/homebrew/opt/llvm/bin",          # macOS arm64 Homebrew keg
    "/usr/local/opt/llvm/bin",             # macOS x86_64 Homebrew keg
    "/usr/lib/llvm/bin",
    "/usr/bin",
]
if sys.platform == "win32":
    _LLVM_SEARCH_DIRS += [
        r"C:\Program Files\LLVM\bin",
        r"C:\Program Files (x86)\LLVM\bin",
    ]


class Rp2040LlvmToolchain(ExternalToolchain):
    """LLVM-based toolchain for the RP2040 (ARM Cortex-M0+)."""

    SUPPORTED = ("rp2040", "cortex-m0plus", "cortex-m0+", "cortex-m0",
                 "rp2350", "cortex-m33", "cortex-m33f", "arm")

    def __init__(self, console: Console, chip: str = "rp2040"):
        super().__init__(console, chip)

    @classmethod
    def supports(cls, chip: str) -> bool:
        return chip.lower() in cls.SUPPORTED

    def get_name(self) -> str:
        return "llvm-rp2040"

    def is_cached(self) -> bool:
        try:
            for b in _REQUIRED_BINS:
                self._find_bin(b)
            return True
        except FileNotFoundError:
            return False

    def install(self) -> None:
        # The vendored pymcu-arm-toolchain wheel (or a system LLVM) provides
        # the binaries. If the wheel is installed but its binaries have not been
        # staged yet, stage them now (download the pinned LLVM into the cache).
        if not self.is_cached():
            self._try_stage_wheel()

        missing = []
        for b in _REQUIRED_BINS:
            try:
                self._find_bin(b)
            except FileNotFoundError:
                missing.append(b)
        if missing:
            raise RuntimeError(
                "LLVM tools not found: " + ", ".join(missing) + ".\n"
                "Install the vendored toolchain (pip install pymcu[rp2040]) and run\n"
                "  python -m pymcu_arm_toolchain fetch --cache\n"
                "or provide a system LLVM (e.g. `brew install llvm lld`)."
            )

    def _try_stage_wheel(self) -> None:
        """Ask the vendored wheel to stage its LLVM tools into the cache."""
        try:
            from pymcu_arm_toolchain._fetch import fetch  # noqa: PLC0415
            fetch(target="cache", console=self.console)
        except Exception:
            # Wheel absent or staging failed; _find_bin falls back to PATH and
            # raises a clear error below if nothing is available.
            pass

    # ── binary / runtime resolution ──────────────────────────────────────────

    def _find_bin_from_wheel(self, name: str) -> Optional[str]:
        """Resolve *name* via the vendored pymcu-arm-toolchain wheel, if present.

        The wheel (analogue of pymcu-avr-toolchain) bundles the LLVM tools or
        stages them into the shared cache and exposes get_tool(). It is the
        authoritative, reproducible source; system LLVM is only a fallback.
        """
        try:
            import pymcu_arm_toolchain as _whl  # noqa: PLC0415
            return str(_whl.get_tool(name))
        except (ImportError, FileNotFoundError):
            return None

    def _wheel_bin_dir(self) -> Optional[Path]:
        """Vendored toolchain wheel cache: ~/.pymcu/tools/<platform>/llvm-rp2040/bin."""
        cand = self._get_tool_dir() / "bin"
        return cand if cand.exists() else None

    def _find_bin(self, name: str) -> str:
        exe = name + (".exe" if sys.platform == "win32" else "")
        from_wheel = self._find_bin_from_wheel(name)
        if from_wheel is not None:
            return from_wheel
        wheel = self._wheel_bin_dir()
        if wheel is not None and (wheel / exe).exists():
            return str(wheel / exe)
        for d in _LLVM_SEARCH_DIRS:
            p = Path(d) / exe
            if p.exists():
                return str(p)
        found = shutil.which(name)
        if found:
            return found
        raise FileNotFoundError(
            f"Required LLVM tool '{name}' not found (pymcu-arm-toolchain "
            f"wheel, cache, {', '.join(_LLVM_SEARCH_DIRS)}, or PATH)."
        )

    def _runtime_dir(self) -> Path:
        """Locate the runtime sources (boot2.S, crt0.S, rp2040.ld)."""
        # 1. Bundled next to this module (wheel layout).
        bundled = Path(__file__).parent / "runtime"
        if (bundled / "rp2040.ld").exists():
            return bundled
        # 2. Development checkout: extensions/pymcu-arm/src/runtime.
        #    __file__ = .../src/python/pymcu/toolchain/rp2040/llvm.py
        ext_root = Path(__file__).parents[5]   # .../extensions/pymcu-arm
        dev = ext_root / "src" / "runtime"
        if (dev / "rp2040.ld").exists():
            return dev
        raise FileNotFoundError(
            "RP2040 runtime sources (boot2.S, crt0.S, rp2040.ld) not found."
        )

    # ── pipeline ─────────────────────────────────────────────────────────────

    def _run(self, cmd: list[str]) -> None:
        try:
            # encoding pinned to utf-8: LLVM tools emit utf-8 diagnostics, and
            # text=True alone decodes with the locale codepage (cp1252 on
            # Windows), which raises UnicodeDecodeError on non-ASCII output.
            subprocess.run(
                cmd, check=True, capture_output=True,
                text=True, encoding="utf-8", errors="replace",
            )
        except subprocess.CalledProcessError as e:
            raise RuntimeError(
                f"RP2040 toolchain step failed: {' '.join(cmd)}\n{e.stderr}"
            ) from e

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Drive the full LLVM pipeline on the backend's LLVM IR (passed as
        *asm_file*, named firmware.asm by the driver but containing .ll text) and
        return the path to the linked flat flash image (firmware.bin).
        """
        ll_file = Path(asm_file)
        out_dir = ll_file.parent
        rt = self._runtime_dir()

        triple, cpu = _resolve_target(self.chip)
        is_rp2350 = (self.chip or "").lower() == "rp2350"

        opt = self._find_bin("opt")
        llc = self._find_bin("llc")
        mc = self._find_bin("llvm-mc")
        ld = self._find_bin("ld.lld")
        objcopy = self._find_bin("llvm-objcopy")

        opt_ll = out_dir / "firmware.opt.ll"
        fw_o = out_dir / "firmware.o"
        elf = out_dir / "firmware.elf"
        binimg = output_file or (out_dir / "firmware.bin")

        # 1. Mid-level optimization (mem2reg, instcombine, ...).
        self._run([opt, "-O2", "-S", str(ll_file), "-o", str(opt_ll)])

        # 2. Compile IR -> Thumb object. RP2350 (M33) compiles softfp: the
        #    calling convention stays soft (matches the datalayout the backend
        #    emits and the crt0 runtime), but f32 arithmetic selects the FPU
        #    (FPv5-SP, VADD.F32/VCVT/VCMP) that cortex-m33 carries by default.
        #    crt0_m33.S enables CPACR before main. RP2040 (M0+) has no FPU;
        #    f32 lowers to __aeabi_f* libcalls over the bootrom fast-float shims.
        llc_cmd = [llc, f"-mtriple={triple}", f"-mcpu={cpu}",
                   "-O2", "-filetype=obj"]
        if is_rp2350:
            llc_cmd += ["-float-abi=soft"]
        else:
            # thumbv6m (Cortex-M0/M0+) has no TBB/TBH hardware jump-table
            # instruction (ARMv7-M+ only), so llc falls back to a hand-rolled
            # "ADD Rd, PC; LDRB; LSL; ADD PC, Rd" sequence for a dense `switch`.
            # At -O2 that sequence is miscompiled on this target: the exception
            # args-print dispatch (#550), a 4-site `switch i8` SimplifyCFG builds
            # from the per-raise-site if/else chain, landed in the wrong case --
            # confirmed by bisecting opt/llc optimization levels independently
            # (opt -O2 + llc -O0 is correct, opt -O0 + llc -O2 is correct, both
            # at -O2 together is not) and by raising this same threshold, which
            # makes llc fall back to a compare chain and fixes it. RP2350
            # (cortex-m33, thumbv8m) keeps real jump tables -- it has TBB/TBH.
            llc_cmd += ["-min-jump-table-entries=1000000"]
        llc_cmd += [str(opt_ll), "-o", str(fw_o)]
        self._run(llc_cmd)

        # 3. Assemble the runtime + link with the per-chip layout.
        if is_rp2350:
            # RP2350: no boot2/CRC stub. The BootROM scans for a picobin
            # IMAGE_DEF block and boots the vector table at flash offset 0.
            crt0_o = out_dir / "crt0.o"
            picobin_o = out_dir / "picobin.o"
            self._run([mc, f"-triple={triple}", "-filetype=obj",
                       str(rt / "crt0_m33.S"), "-o", str(crt0_o)])
            self._run([mc, f"-triple={triple}", "-filetype=obj",
                       str(rt / "picobin_rp2350.S"), "-o", str(picobin_o)])
            self._run([ld, "-T", str(rt / "rp2350.ld"),
                       str(crt0_o), str(picobin_o), str(fw_o), "-o", str(elf)])
        else:
            # RP2040: boot2 @0x000, vectors @0x100.
            boot2_o = out_dir / "boot2.o"
            crt0_o = out_dir / "crt0.o"
            self._run([mc, f"-triple={triple}", "-filetype=obj",
                       str(rt / "boot2.S"), "-o", str(boot2_o)])
            self._run([mc, f"-triple={triple}", "-filetype=obj",
                       str(rt / "crt0.S"), "-o", str(crt0_o)])
            self._run([ld, "-T", str(rt / "rp2040.ld"),
                       str(boot2_o), str(crt0_o), str(fw_o), "-o", str(elf)])

        # 5. Flatten to a raw flash image.
        self._run([objcopy, "-O", "binary", str(elf), str(binimg)])

        # 6. Pack the same image as UF2 so `pymcu flash` can drag-and-drop it
        #    onto the RPI-RP2 BOOTSEL volume (or hand it to picotool) with no
        #    offset and no external tool.
        write_uf2(Path(binimg), Path(binimg).with_suffix(".uf2"), self.chip)

        return Path(binimg)

    # ── native-module (.mpy) object ──────────────────────────────────────────

    @staticmethod
    def natmod_arch(chip: str) -> tuple[str, str, str, list[str]]:
        """(mpy arch name, llvm triple, llvm cpu, extra llc flags) for a native module."""
        try:
            return _NATMOD_ARCHES[(chip or "").lower()]
        except KeyError:
            raise RuntimeError(
                f"No native-module architecture is defined for target '{chip}'. "
                f"Supported: {', '.join(sorted(_NATMOD_ARCHES))}."
            ) from None

    def assemble_natmod(self, ll_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Compile the backend's LLVM IR into a RELOCATABLE OBJECT for a MicroPython /
        CircuitPython native module, and return its path.

        This is the whole difference from assemble(): no link, so no entry point, no
        crt0, no vector table, no linker script and no libc. CircuitPython's
        tools/mpy_ld.py is the linker, and it places the code itself at load time.

        Two things it needs that a firmware object does not:

          * position independence. Every reference that leaves the object has to go
            through the GOT, because R_ARM_GOT_BREL is the ONLY relocation mpy_ld.py
            accepts for a symbol it has to resolve. `-relocation-model=pic` is what
            makes llc emit that form.
          * only .text / .rodata / .data.rel.ro / .bss. mpy_ld.py refuses a non-empty
            .data outright, and silently ignores anything else. The unwind tables are
            dropped here rather than left to be ignored, so that what ships is only
            what the loader will look at.
        """
        ll_file = Path(ll_file)
        out_dir = ll_file.parent
        obj = Path(output_file) if output_file else (out_dir / (ll_file.stem + ".o"))

        _, triple, cpu, extra = self.natmod_arch(self.chip)

        opt = self._find_bin("opt")
        llc = self._find_bin("llc")
        objcopy = self._find_bin("llvm-objcopy")

        opt_ll = out_dir / (ll_file.stem + ".opt.ll")
        self._run([opt, "-O2", "-S", str(ll_file), "-o", str(opt_ll)])

        natmod_llc_cmd = [
            llc, f"-mtriple={triple}", f"-mcpu={cpu}",
            "-O2", "-filetype=obj",
            # The one flag the whole mode turns on.
            "-relocation-model=pic",
        ]
        if triple.startswith("thumbv6m"):
            # Same llc jump-table miscompile as assemble() below -- see the
            # comment there (#550). armv6m (rp2040's native-module arch) has
            # no TBB/TBH either, so it hits the same broken fallback sequence.
            natmod_llc_cmd += ["-min-jump-table-entries=1000000"]
        natmod_llc_cmd += [*extra, str(opt_ll), "-o", str(obj)]
        self._run(natmod_llc_cmd)

        # .ARM.exidx is unwind data for a runtime that is not there. mpy_ld.py skips
        # it (it is neither PROGBITS nor NOBITS), but an object that carries sections
        # the loader will never map invites the next reader to wonder whether it does.
        self._run([objcopy, "--remove-section=.ARM.exidx*", str(obj)])

        return obj

    def link(self, hex_file: Path, chip: str, output_dir: Path):
        """ELF + size report are produced as a side effect of assemble(); the
        ELF lives next to the .bin. Report flash usage from the binary size."""
        elf = Path(output_dir) / "firmware.elf"
        binimg = Path(output_dir) / "firmware.bin"
        if not binimg.exists():
            return None
        size = binimg.stat().st_size
        report = f"flash: {size} bytes ({size / 1024:.1f} KiB)"
        return (elf if elf.exists() else binimg), report
