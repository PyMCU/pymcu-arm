using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// An exception raised inside a native module reaches CircuitPython as the Python exception of
/// the same type and message. The expected text is whatever CPython prints for the same kernel
/// run as an ordinary module; the actual text comes from the module running in CircuitPython 10.3.1
/// on the RP2040 emulator.
/// </summary>
[TestFixture]
public class NatmodExceptionTests
{
    // Calls f(*a) and prints either the result or the exception's type and args.
    private const string Helpers = """
        def t(f, *a):
            try:
                print(f(*a))
            except Exception as e:
                print(type(e).__name__, e.args)


        def tn(f, *a):
            try:
                print(f(*a))
            except Exception as e:
                print(type(e).__name__)

        """;

    private static void Check(string kernel, string module, string exercise, bool needsCPython314 = false)
    {
        NatmodHarness.RequireWorkingEmulator();
        if (needsCPython314) NatmodHarness.RequireCPython(3, 14);
        var code = $"import {module}\n{Helpers}\n{exercise}";
        var expected = NatmodHarness.RunOnCPython(kernel, module, code);
        var mpy = NatmodHarness.BuildMpy(kernel, module);
        var actual = NatmodHarness.RunOnCircuitPython(module, mpy, code);
        TestContext.Out.WriteLine(actual);
        actual.Should().Be(expected, "the module must behave as the same Python does under CPython");
    }

    [Test]
    public void RaiseWithMessage_ArrivesAsThatExceptionWithThatMessage()
        => Check("""
            from pymcu.types import int16


            def check(x: int16) -> int16:
                if x < 0:
                    raise ValueError("negative")
                return x
            """, "nx1", """
            t(nx1.check, 5)
            t(nx1.check, -1)
            t(nx1.check, 7)
            """);

    [Test]
    public void DivisionByZero_RaisesZeroDivisionErrorWithCPythonsText()
        => Check("""
            from pymcu.types import int16


            def div(a: int16, b: int16) -> int16:
                return a // b


            def mod(a: int16, b: int16) -> int16:
                return a % b
            """, "nx2", """
            t(nx2.div, 7, 2)
            t(nx2.div, -7, 2)
            t(nx2.div, 7, 0)
            t(nx2.mod, 7, 3)
            t(nx2.mod, -7, 3)
            t(nx2.mod, 7, 0)
            t(nx2.div, 9, 3)
            """, needsCPython314: true);

    [Test]
    public void TryExceptInsideTheKernel_HandlesItWithoutLeavingTheModule()
        => Check("""
            from pymcu.types import int16


            def safe_div(a: int16, b: int16) -> int16:
                r: int16 = -1
                try:
                    r = a // b
                except ZeroDivisionError:
                    r = -1
                return r


            def guarded(x: int16) -> int16:
                r: int16 = 0
                try:
                    if x < 0:
                        raise ValueError("neg")
                    r = x
                except ValueError:
                    r = 99
                return r
            """, "nx3", """
            t(nx3.safe_div, 8, 2)
            t(nx3.safe_div, 8, 0)
            t(nx3.guarded, 5)
            t(nx3.guarded, -5)
            t(nx3.safe_div, 9, 3)
            """);

    [Test]
    public void RaiseInAnInternalFunction_PropagatesToTheExportedOne()
        => Check("""
            from pymcu.types import int16


            def inner(x: int16) -> int16:
                if x == 0:
                    raise RuntimeError("zero not allowed")
                return 100 // x


            def outer(x: int16) -> int16:
                return inner(x) + 1
            """, "nx4", """
            t(nx4.outer, 5)
            t(nx4.outer, 0)
            t(nx4.outer, 20)
            """);

    [Test]
    public void EveryBuiltinTypeTheCompilerCanRaise_ArrivesAsItself()
        => Check("""
            from pymcu.types import int16


            def pick(n: int16) -> int16:
                if n == 1:
                    raise ValueError("v")
                if n == 2:
                    raise TypeError("t")
                if n == 3:
                    raise IndexError("i")
                if n == 4:
                    raise KeyError("k")
                if n == 5:
                    raise RuntimeError("r")
                if n == 6:
                    raise OverflowError("o")
                if n == 7:
                    raise NotImplementedError("n")
                if n == 8:
                    raise ZeroDivisionError("z")
                return n
            """, "nx5", """
            for i in range(0, 10):
                t(nx5.pick, i)
            """);

    [Test]
    public void RaiseWithoutAMessage_ArrivesWithNoArguments()
        => Check("""
            from pymcu.types import int16


            def need_positive(x: int16) -> int16:
                if x <= 0:
                    raise ValueError
                return x


            def need_small(x: int16) -> int16:
                if x > 100:
                    raise OverflowError
                return x
            """, "nx6", """
            t(nx6.need_positive, 3)
            t(nx6.need_positive, 0)
            t(nx6.need_small, 101)
            t(nx6.need_small, 100)
            t(nx6.need_positive, 4)
            """);

    [Test]
    public void ABufferExport_RaisesAfterReadingItsOwnLength()
        => Check("""
            from pymcu.types import int16


            def first_positive(buf: bytes) -> int16:
                i: int16 = 0
                while i < len(buf):
                    if buf[i] != 0:
                        return buf[i]
                    i = i + 1
                raise ValueError("all zero")
            """, "nx7", """
            t(nx7.first_positive, bytes([0, 0, 7, 9]))
            t(nx7.first_positive, bytes([0, 0, 0]))
            t(nx7.first_positive, bytes([5]))
            """);
}
