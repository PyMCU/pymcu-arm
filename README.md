# pymcu-arm

ARM (Cortex-M) support for the PyMCU compiler: LLVM-IR codegen backend + LLVM toolchain driver.

Unlike the AVR/PIC/RISC-V backends, this backend does **not** emit assembly directly. It lowers PyMCU's architecture-agnostic IR (`.mir`) to **LLVM IR text (`.ll`)** and hands it to LLVM — so register allocation, instruction selection, the AAPCS calling convention, and all optimization passes are done by LLVM.

Currently supported targets:

| Target | Triple | CPU |
|---|---|---|
| RP2040 (Raspberry Pi Pico) | `thumbv6m-none-eabi` | `cortex-m0plus` |

The codegen is chip-agnostic: only `-mtriple`/`-mcpu` change per target.

## Pipeline

```
pymcuc --emit-ir        →  firmware.mir    (target-agnostic IR, 32-bit pointers)
pymcuc-arm (this pkg)   →  firmware.ll     (LLVM IR text)
LLVM toolchain          →  opt → llc → ld.lld → llvm-objcopy
                            + generic boot2 (crc32) + crt0  →  firmware.bin
```

The flat flash image (`firmware.bin`, boot2 at offset 0) is what the RP2040Sharp
emulator's `PicoSimulation.LoadFlash(...)` consumes in the integration tests.

## Installation

```bash
pip install pymcu-arm
```

LLVM tools are resolved (in order) from the `pymcu-arm-toolchain` wheel cache,
common system install paths (`/opt/homebrew/opt/llvm/bin`, `/usr/lib/llvm/bin`),
or `PATH`. A system LLVM (`brew install llvm lld` / `apt install llvm lld`) also
works with no extra package.

## Layout

```
src/python/pymcu/backend/rp2040/    backend plugin — wraps pymcuc-arm
src/python/pymcu/toolchain/rp2040/  LLVM toolchain driver (opt → firmware.bin)
src/csharp/lib/                     Rp2040BackendProvider + Targets/RP2040/Rp2040LlvmCodeGen
src/csharp/cli/                     pymcuc-arm runner CLI
src/runtime/                        generic boot2 (crc32), crt0, rp2040.ld linker script
```

## Status

Alpha. MVP scope: single-core (core0), GPIO + UART0. No GC / exceptions / float yet.
