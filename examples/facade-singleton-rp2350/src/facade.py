# Re-export a class through a facade (like hal/wifi.py -> hal/rp2350/cyw43).
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
if __CHIP__.name == "rp2350":
    from concrete import Radio
else:
    raise CompileError("unsupported")
