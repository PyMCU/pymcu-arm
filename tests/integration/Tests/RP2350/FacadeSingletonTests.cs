using FluentAssertions;
using NUnit.Framework;
using RP2350.Peripherals;
using RP2350.TestKit;

namespace PyMCU.IntegrationTests.Tests.RP2350;

/// <summary>
/// Regression for the facade-re-export singleton dispatch fix: a module-level singleton
/// (`wifi_mod.radio = Radio()`) whose class is imported through a facade re-export
/// (facade.py does `from concrete import Radio`) must dispatch its methods to the
/// concrete class. Before the fix, `radio.light()` mangled to the nonexistent
/// `radio_light` ("call to undefined function") because the singleton's class wasn't
/// tracked across the facade. This is exactly the shape of CircuitPython's `wifi.radio`.
/// </summary>
[TestFixture]
public class FacadeSingletonTests
{
    [Test]
    public void FacadeReexportedSingleton_DispatchesToConcreteClass()
    {
        // The build itself is the assertion: it used to throw a CompileError.
        var fw = PymcuCompiler.BuildRp2350("facade-singleton-rp2350");
        using var sim = RP2350TestSimulation.Create(CpuArchitecture.Arm).WithBinary(fw);
        sim.RunMilliseconds(5);
        sim.HardFaultCount.Should().Be(0);
        sim.IsLockedUp.Should().BeFalse();
    }
}
