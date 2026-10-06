# Changelog

All notable changes to pymcu-arm, the ARM (RP2040 / RP2350) backend and LLVM toolchain
driver for PyMCU.

The ARM backend is still **alpha**. pymcu-compiler, pymcu-stdlib, pymcu-sdk and the AVR
backend are in beta; ARM stays behind them on purpose.

## 0.1.0a6

### Requirements

- **pymcu-compiler 0.1.0b1 or newer.** The native-module mode below is driven by
  `pymcu natmod`, which first ships in pymcu-compiler 0.1.0b1. This package does not
  declare pymcu-compiler as a dependency (pymcu-compiler lists pymcu-arm as an optional
  extra), so install them together: `pip install "pymcu-compiler>=0.1.0b1" pymcu-arm`.
- pymcu-sdk `>=0.1.0a3`, unchanged. The Python SDK surface this package uses
  (`BackendPlugin`, `LicenseStatus`, `ToolchainPlugin`, `ExternalToolchain`,
  `HardwareProgrammer`) is the same in 0.1.0a3 and 0.1.0b1. pymcu-compiler 0.1.0b1
  already requires pymcu-sdk `>=0.1.0b1`, so a real install resolves to that.

### Added

- **Native-module mode for `pymcu natmod` (experimental).** The toolchain can now stop
  before the link and emit a relocatable, position-independent object
  (`assemble_natmod`), which CircuitPython's `tools/mpy_ld.py` links into a `.mpy` native
  module. With 0.1.0a5, `pymcu natmod` stopped with "the installed ARM toolchain has no
  native-module mode".
  - rp2040 builds `armv6m` (as Cortex-M0) and rp2350 builds `armv7emsp` (as Cortex-M4).
    The `.mpy` header is a promise to every board of that architecture, so the object is
    built for the architecture the header names, not for the chip's own core.
  - Float ABI flags follow CircuitPython's `py/dynruntime.mk` for each architecture.
  - Measured: the natmod-plasma example compiles to 784 B (rp2350) and 1032 B (rp2040),
    byte-identical to the modules validated on a Pico under CircuitPython 10.3.1.
  - Experimental: validated on CircuitPython 10.3.1 only, and only for integer kernels.
    `pymcu natmod` itself also needs, outside this package, the CircuitPython source tree
    of the version the board runs, `arm-none-eabi-gcc`, and the Python modules
    `pyelftools` and `ar`; the driver says which one is missing.
- **Pico W WiFi.** `examples/wifi-cyw43-rp2040` is the same `main.py` as the Pico 2 W
  example, byte for byte, through the `pymcu.hal.wifi` facade, plus a WPA2 example
  (`examples/wifi-wpa2-rp2040`) that uses only the public API. Integration tests check
  that the Pico W reaches chip-alive and joins on its own register map, that the WPA2 join
  is the ten ioctls in order, and that the SDPCM sequence advances on every F2 packet.
  These run against an emulator of the CYW43439, which has no clock: they check sequences,
  not timing, and are not a substitute for a test on a real Pico W.

### Fixed

- `not x` on a value known only at run time produced LLVM IR that `opt` rejected, so the
  build failed. The CircuitPython blink `led.value = not led.value` is the common way to
  reach it.
- `print()` of a float, and a module-level `x: float = 0.0`, failed the build in `opt`:
  the backend wrote an integer `0` where LLVM needs a float zero, and lowered
  `bitcast(uint32, f)` (which the stdlib's float formatting uses) as an integer copy.
  Both now emit valid IR; `print(10 / 4.0)` prints `2.5` in the simulator.
- An uncaught builtin exception above code 5 (for example `ZeroDivisionError`) was
  reported as `Exception6` instead of its name. The name list now comes from the SDK.
- The WiFi examples import the `pymcu.hal.wifi` facade; the chip-specific module they
  used no longer exists.
- Builds on a case-sensitive filesystem: the sibling monorepo is referenced as `PyMCU`,
  its real name, and a missing sibling repository now fails first with error
  `PYMCUSIB001` naming what to clone, instead of being dropped with a warning.

### CI

- The "Integration Tests (RP2040)" job no longer fails on every run for lack of the
  private RP2350Sharp repository. Without an `RP2350SHARP_TOKEN` secret it runs the
  RP2040 half of the suite, leaves the RP2350 half out, and says so in a warning and in
  the run summary. With the secret it runs the whole suite.

### Known issues

Measured with pymcu-compiler, pymcu-stdlib, pymcu-circuitpython and pymcu-micropython
0.1.0b1 from PyPI and this wheel: the integration suite passes 71 of 82. The 11 failures
are drift between the layers and the ARM HAL, not code generation, and all fail at compile
time with a diagnostic:

- MicroPython layer: `Pin.mode()` is not available on ARM
  ("call to undefined function 'machine_Pin_mode'"), which stops the DHT examples
  (`dht-async-rp2350`, `dht-mqtt-rp2350`, `dht-mqtt-mp-rp2350`; 5 tests), and
  `mp-uart-echo` calls `uart.println()`, which the layer's `machine.UART` does not
  provide on the RP2040 (3 tests).
- CircuitPython layer: `busio` only wires I2C for AVR, so `cp-digitalio-uart` stops at
  "call to undefined function '_hal_i2c_stop'" on the RP2040 (3 tests).
- pymcu-circuitpython 0.1.0b1 has a `raspberry_pi_pico` board file but none for the
  Pico 2, so `import board` with `board = "raspberry_pi_pico2"` does not resolve its pin
  names.

## 0.1.0a5

Released 2026-08-17. No changelog was kept before this file; see the git history up to
tag `v0.1.0a5`.
