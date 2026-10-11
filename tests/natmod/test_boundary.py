"""What a native module returns on an RP2040, against what CPython returns for the same source.

The kernel is compiled by `pymcu natmod`, baked onto CIRCUITPY inside a CircuitPython 10.3.1
image, booted in RP2040Sharp, imported, and each expression is evaluated at the REPL. The
oracle is the SAME source file executed by CPython, so a value that disagrees is the
compiler's or the adapter's, not a hand-written expectation.

It needs five things that a plain checkout does not have, and skips, naming the missing one:

    PYMCU_CIRCUITPYTHON   a CircuitPython source tree at the tag the image is built from
    PYMCU_CP_UF2          that image for the Pico (default: the oracle cache)
    RP2040SHARP_SRC       an RP2040Sharp checkout WITH the ADD high-register PC fix (f622e3c);
                          without it a natmod imports as an empty module
    dotnet, arm-none-eabi-gcc, and a `pymcu` driver (PYMCU_BIN, or on PATH)

Floats cross as single precision, so the oracle rounds its answer to 32 bits and the
comparison allows the 7 significant digits CircuitPython prints.
"""

import json
import math
import os
import shutil
import struct
import subprocess
import textwrap
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent


def _need(name: str, value) -> str:
    if not value:
        pytest.skip(f"{name} is not available")
    return value


def _env() -> dict:
    cp = os.environ.get("PYMCU_CIRCUITPYTHON")
    if not cp or not (Path(cp) / "py" / "dynruntime.h").exists():
        pytest.skip("PYMCU_CIRCUITPYTHON does not point at a CircuitPython source tree")
    uf2 = os.environ.get("PYMCU_CP_UF2") or str(
        Path.home() / ".cache" / "pymcu-oracle"
        / "adafruit-circuitpython-raspberry_pi_pico-en_US-10.3.1.uf2")
    if not Path(uf2).exists():
        pytest.skip("no CircuitPython Pico UF2 (set PYMCU_CP_UF2)")
    src = os.environ.get("RP2040SHARP_SRC")
    if not src or not (Path(src) / "src" / "RP2040.TestKit").exists():
        pytest.skip("RP2040SHARP_SRC does not point at an RP2040Sharp checkout "
                    "(it must carry f622e3c, the ADD high-register PC read)")
    _need("dotnet", shutil.which("dotnet"))
    _need("arm-none-eabi-gcc", shutil.which("arm-none-eabi-gcc"))
    pymcu = os.environ.get("PYMCU_BIN") or shutil.which("pymcu")
    _need("the pymcu driver (PYMCU_BIN)", pymcu)
    return {"cp": cp, "uf2": uf2, "sharp": src, "pymcu": pymcu}


def f32(x: float) -> float:
    try:
        return struct.unpack("f", struct.pack("f", x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)


class Raises:
    def __init__(self, name: str):
        self.name = name


def oracle(source: str, expr: str):
    """The same kernel file under CPython, with PyMCU's width names as plain ints."""
    ns: dict = {"__builtins__": __builtins__}
    for w in ("int8", "int16", "int32", "uint8", "uint16", "uint32"):
        ns[w] = int
    exec(compile("from __future__ import annotations\n" + source, "kernel", "exec"), ns)
    return eval(expr, ns)


def run_case(tmp_path: Path, source: str, name: str, exprs: list[str]) -> dict:
    env = _env()
    proj = tmp_path / name
    (proj / "src").mkdir(parents=True)
    (proj / "src" / "main.py").write_text(source)
    (proj / "pyproject.toml").write_text(
        f'[project]\nname = "{name}"\nversion = "0.1.0"\n\n[tool.pymcu]\n'
        f'target = "rp2040"\nsources = "src"\n\n[tool.pymcu.natmod]\nmodule = "{name}"\n')
    built = subprocess.run([env["pymcu"], "natmod", "--circuitpython", env["cp"]],
                           cwd=proj, capture_output=True, text=True)
    assert built.returncode == 0, built.stdout + built.stderr
    mpy = proj / "dist" / f"{name}.mpy"

    probe = HERE / "probe"
    out_dir = tmp_path / "probe-out"
    b = subprocess.run(["dotnet", "build", str(probe / "Probe.csproj"), "-c", "Release",
                        "--nologo", "-o", str(out_dir), f"-p:RP2040SHARP_SRC={env['sharp']}"],
                       capture_output=True, text=True)
    assert b.returncode == 0, b.stdout[-2000:] + b.stderr[-2000:]
    ran = subprocess.run(["dotnet", str(out_dir / "Probe.dll"), env["uf2"], str(mpy), name,
                          *[f"{name}.{e}" for e in exprs]],
                         capture_output=True, text=True)
    assert ran.returncode == 0, ran.stdout + ran.stderr

    got: dict = {"dir": "", "expr": {}}
    for line in ran.stdout.splitlines():
        if line.startswith("DIR "):
            got["dir"] = json.loads(line[4:])
        elif line.startswith("EXPR "):
            rec = json.loads(line[5:])
            got["expr"][rec["expr"]] = rec["out"]
    return got


def repl_value(out: str, expr: str) -> str:
    """The REPL text after the echoed expression, up to the next prompt."""
    lines = [l.rstrip() for l in out.replace("\r", "").split("\n")]
    after = []
    seen = False
    for l in lines:
        if not seen:
            if l.endswith(expr):
                seen = True
            continue
        if l.startswith(">>>"):
            break
        after.append(l)
    return "\n".join(x for x in after if x != "").strip()


def check(got: dict, name: str, source: str, cases: list, exports: list[str]):
    if not all(e in got["dir"] for e in exports):
        pytest.skip(f"the module imported without its exports ({got['dir']!r}): the emulator "
                    "lacks f622e3c (ADD high-register PC read), which natmod loading needs")
    for expr, expected in cases:
        text = repl_value(got["expr"][f"{name}.{expr}"], f"{name}.{expr}")
        want = expected if expected is not None else oracle(source, expr)
        if isinstance(want, Raises):
            assert want.name in text, f"{expr}: wanted {want.name}, REPL said {text!r}"
        elif isinstance(want, bool):
            assert text == str(want), f"{expr}: CPython {want}, native {text!r}"
        elif isinstance(want, float):
            have = float(text)
            w = f32(want)
            if math.isnan(w):
                assert math.isnan(have), f"{expr}: native {text!r}"
            elif math.isinf(w):
                assert have == w, f"{expr}: native {text!r}"
            else:
                assert have == pytest.approx(w, rel=2e-6, abs=1e-6), \
                    f"{expr}: CPython(f32) {w!r}, native {text!r}"
        else:
            assert text == str(want), f"{expr}: CPython {want!r}, native {text!r}"


CONSTS = textwrap.dedent("""\
    K: int = 7
    BIG: int32 = 100000

    def F(a: int) -> int:
        return a + K

    def G(a: int32) -> int32:
        return a + BIG
""")

BOOLS = textwrap.dedent("""\
    def notf(a: bool) -> bool:
        return not a

    def isbig(a: int) -> bool:
        return a > 100

    def pick(a: bool, n: int) -> int:
        if a:
            return n
        return 0 - n
""")

FLOATS = textwrap.dedent("""\
    def mix(x: float, y: float) -> float:
        return x * 2.0 + y

    def to_i(x: float) -> int:
        return int(x)

    def from_i(n: int) -> float:
        return n * 0.5

    def scale(x: float, n: int) -> float:
        return x * n
""")


def test_a_scalar_module_constant_folds_into_the_kernel(tmp_path):
    cases = [("F(5)", None), ("F(-3)", None), ("F(0)", None), ("G(1)", None), ("G(-100000)", None)]
    got = run_case(tmp_path, CONSTS, "consts", [c[0] for c in cases])
    check(got, "consts", CONSTS, cases, ["F", "G"])


def test_a_bool_crosses_as_truth(tmp_path):
    cases = [
        ("notf(True)", None), ("notf(False)", None), ("notf(2)", None), ("notf(0)", None),
        ("notf([])", None), ("notf(None)", None),
        ("isbig(101)", None), ("isbig(100)", None),
        ("pick(True, 5)", None), ("pick(False, 5)", None), ("pick(1, -4)", None),
    ]
    got = run_case(tmp_path, BOOLS, "bools", [c[0] for c in cases])
    check(got, "bools", BOOLS, cases, ["notf", "isbig", "pick"])


def test_a_float_crosses_as_single_precision(tmp_path):
    cases = [
        ("mix(1.5, 0.25)", None), ("mix(-3.0, 1e10)", None), ("mix(1, 2)", None),
        ("mix(float('inf'), 1.0)", None), ("mix(float('nan'), 1.0)", None),
        ("to_i(7.9)", None), ("to_i(-7.9)", None),
        ("from_i(5)", None), ("from_i(-3)", None),
        ("scale(1.1, 3)", None),
        ("mix('a', 2)", Raises("TypeError")),
    ]
    got = run_case(tmp_path, FLOATS, "floats", [c[0] for c in cases])
    check(got, "floats", FLOATS, cases, ["mix", "to_i", "from_i", "scale"])
