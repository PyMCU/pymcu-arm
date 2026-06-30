// SPDX-License-Identifier: MIT
// Rp2040LlvmCodeGen - lowers the architecture-agnostic PyMCU IR (ProgramIR)
// to LLVM IR text (.ll) for the RP2040 (ARM Cortex-M0+, thumbv6m-none-eabi).
//
// Design: emit textual LLVM IR (no LLVMSharp / native libLLVM dependency).
//   * Every named Variable / Temporary becomes an `alloca` in the entry block;
//     LLVM's mem2reg (run by `opt`) promotes them to SSA. We never build SSA
//     ourselves -- LLVM also does register allocation, instruction selection and
//     the AAPCS calling convention.
//   * All computation is done at i32 width (the Cortex-M register width). Loads
//     zero/sign-extend to i32; stores truncate back to the slot's declared width.
//   * MMIO (MemoryAddress) lowers to `inttoptr` + volatile load/store -- the
//     correct model for RP2040's memory-mapped peripherals.
//   * The flat PyMCU instruction list (labels + jumps with implicit fall-through)
//     is converted to a well-formed LLVM CFG (every basic block terminated).
//
// MVP subset: GPIO + UART (Copy/Binary/Unary/AugAssign, MMIO load/store, bit
// ops, control flow, direct calls). GC, exceptions, float, vtables, arrays and
// operand-form inline asm throw NotSupportedException with a clear message.

using System.Text;
using PyMCU.Common.Models;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.RP2040;

public class Rp2040LlvmCodeGen(DeviceConfig cfg) : CodeGen
{
    // Per-target LLVM triple + cpu. The codegen is otherwise chip-agnostic: only
    // these two strings change per Cortex-M target. RP2350 is compiled soft-float
    // (float is unsupported anyway), so the datalayout below is valid for both.
    private static readonly Dictionary<string, (string Triple, string Cpu)> Targets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["rp2040"]       = ("thumbv6m-none-eabi", "cortex-m0plus"),
            ["cortex-m0plus"] = ("thumbv6m-none-eabi", "cortex-m0plus"),
            ["rp2350"]       = ("thumbv8m.main-none-eabi", "cortex-m33"),
            ["cortex-m33"]   = ("thumbv8m.main-none-eabi", "cortex-m33"),
        };

    private const string DataLayout   = "e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64";

    private readonly DeviceConfig _cfg = cfg;

    // Resolve the LLVM triple for this device: prefer the concrete chip, fall
    // back to the arch, then default to RP2040 (Cortex-M0+) to preserve the
    // historical behaviour when neither is recognised.
    private string TargetTriple => ResolveTarget().Triple;

    private (string Triple, string Cpu) ResolveTarget()
    {
        if (!string.IsNullOrEmpty(_cfg.TargetChip) && Targets.TryGetValue(_cfg.TargetChip, out var byChip))
            return byChip;
        if (!string.IsNullOrEmpty(_cfg.Arch) && Targets.TryGetValue(_cfg.Arch, out var byArch))
            return byArch;
        return Targets["rp2040"];
    }

    private TextWriter _out = TextWriter.Null;
    private int _ssa;                                   // fresh SSA / block counter
    private bool _blockOpen;                            // current block needs a terminator
    private HashSet<string> _globals = new();           // module-level global variable names
    private Dictionary<string, List<DataType>> _paramTypes = new();  // func -> param types
    private Dictionary<string, DataType> _returnTypes = new();       // func -> return type
    private Dictionary<string, DataType> _slots = new();            // current func: var/tmp -> type
    private HashSet<string> _nakedFns = new();                      // @naked function names (never tail-call)

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _out = output;
        _nakedFns = new HashSet<string>(program.Functions.Where(f => f.IsNaked).Select(f => f.Name));
        EmitModulePreamble();

        // Precompute signatures (param + return types) for call lowering.
        foreach (var f in program.Functions)
        {
            _returnTypes[f.Name] = f.ReturnType;
            _paramTypes[f.Name]  = InferParamTypes(f);
        }

        // Module-level globals.
        _globals = new HashSet<string>(program.Globals.Select(g => g.Name));
        foreach (var g in program.Globals)
            _out.WriteLine($"@{Sym(g.Name)} = internal global {LlT(g.Type)} 0");
        if (program.Globals.Count > 0) _out.WriteLine();

        // Named fixed-size arrays (ZCA instance backing stores, small buffers) as
        // zero-initialised byte blobs in .bss; ArrayLoad/ArrayStore GEP into them.
        foreach (var (arrName, byteSize) in program.GlobalArrays)
            _out.WriteLine($"@{Sym(arrName)} = internal global [{byteSize} x i8] zeroinitializer");
        if (program.GlobalArrays.Count > 0) _out.WriteLine();

        // The frontend only registers ENTRY-module top-level arrays in GlobalArrays.
        // Function-local fixed arrays and imported-module arrays are still referenced by
        // ArrayStore/ArrayLoad (which carry the element type + Count). PyMCU's static
        // model has no stack/heap arrays, so statically allocate a global for every
        // referenced array that isn't already declared, sized from the instruction.
        var extraArrays = new Dictionary<string, int>();
        foreach (var func in program.Functions)
            foreach (var instr in func.Body)
            {
                string? an = instr switch
                {
                    ArrayStore ast => ast.ArrayName,
                    ArrayLoad al => al.ArrayName,
                    _ => null,
                };
                if (an == null || program.GlobalArrays.ContainsKey(an) || _globals.Contains(an))
                    continue;
                int bytes = instr switch
                {
                    ArrayStore ast => ast.Count * ast.ElemType.SizeOf(),
                    ArrayLoad al => al.Count * al.ElemType.SizeOf(),
                    _ => 0,
                };
                if (!extraArrays.TryGetValue(an, out int cur) || bytes > cur) extraArrays[an] = bytes;
            }
        foreach (var (arrName, byteSize) in extraArrays)
            _out.WriteLine($"@{Sym(arrName)} = internal global [{byteSize} x i8] zeroinitializer");
        if (extraArrays.Count > 0) _out.WriteLine();

        // Emit only functions reachable from a root (main / interrupt / exported).
        // PyMCU does not tree-shake unreachable non-inline functions, so an
        // imported module (e.g. pymcu.time) drops in sibling helpers written for
        // OTHER architectures -- their AVR/PIC inline asm would make llc reject
        // the module. Reachability analysis prunes them here.
        var reachable = ComputeReachable(program);
        foreach (var func in program.Functions)
        {
            if (func.IsInline) continue;   // inlined at call sites; never emitted standalone
            if (!reachable.Contains(func.Name)) continue;
            CompileFunction(func);
        }

        // @export_c functions may have no visible IR caller (they are reached only from
        // inline asm, e.g. an RTOS `asm("bl scheduler")`). Anchor them in @llvm.used so
        // the optimizer's globaldce/internalize pass keeps the symbol in the object.
        var exported = program.Functions
            .Where(f => f.IsExportC && !f.IsInline && reachable.Contains(f.Name))
            .ToList();
        if (exported.Count > 0)
        {
            string elems = string.Join(", ", exported.Select(f => $"ptr @{EmitSym(f)}"));
            _out.WriteLine();
            _out.WriteLine($"@llvm.used = appending global [{exported.Count} x ptr] " +
                           $"[{elems}], section \"llvm.metadata\"");
        }
    }

    // Functions reachable from main / interrupt handlers / @export_c entry points,
    // following direct Calls and address-taken FunctionRefs transitively.
    private static HashSet<string> ComputeReachable(ProgramIR program)
    {
        var byName = new Dictionary<string, Function>();
        foreach (var f in program.Functions) byName[f.Name] = f;

        var reachable = new HashSet<string>();
        var work = new Stack<string>();
        void AddRoot(string name)
        {
            if (byName.ContainsKey(name) && reachable.Add(name)) work.Push(name);
        }

        foreach (var f in program.Functions)
            if (f.Name == "main" || f.IsInterrupt || f.IsExportC || (f.OriginalName == "main"))
                AddRoot(f.Name);

        while (work.Count > 0)
        {
            var f = byName[work.Pop()];
            foreach (var instr in f.Body)
            {
                switch (instr)
                {
                    case Call c: AddRoot(c.FunctionName); break;
                    case VirtualCall vc: AddRoot(vc.MethodName); break;
                }
                foreach (var v in OperandsOf(instr))
                    if (v is FunctionRef fr) AddRoot(fr.FunctionName);
            }
        }
        return reachable;
    }

    private void EmitModulePreamble()
    {
        _out.WriteLine("; PyMCU ARM (Cortex-M) backend - generated LLVM IR");
        _out.WriteLine($"; target chip: {_cfg.TargetChip}  freq: {_cfg.Frequency} Hz");
        _out.WriteLine($"target datalayout = \"{DataLayout}\"");
        _out.WriteLine($"target triple = \"{TargetTriple}\"");
        _out.WriteLine();
    }

    // ── Function lowering ────────────────────────────────────────────────────

    private void CompileFunction(Function func)
    {
        _ssa = 0;
        _slots = CollectSlots(func);
        var paramTypes = _paramTypes[func.Name];

        // Signature.
        var sig = new StringBuilder();
        sig.Append($"define {LlT(func.ReturnType)} @{EmitSym(func)}(");
        for (int i = 0; i < func.Params.Count; i++)
        {
            if (i > 0) sig.Append(", ");
            sig.Append($"{LlT(paramTypes[i])} %arg.{Sym(func.Params[i])}");
        }
        // A @naked function gets full control: LLVM emits no prologue/epilogue and
        // touches no registers around the body, so the inline asm owns the frame
        // (essential for an RTOS context switch). The asm itself returns (bx lr /
        // exception return), so the block ends with `unreachable`.
        sig.Append(func.IsNaked ? ") naked noinline {" : ") {");
        _out.WriteLine();
        _out.WriteLine(sig.ToString());

        _out.WriteLine("entry:");
        if (!func.IsNaked)
        {
            // Allocas for every local slot, then store incoming params.
            foreach (var (name, type) in _slots)
                if (!_globals.Contains(name))
                    _out.WriteLine($"  %{SlotReg(name)} = alloca {LlT(type)}");
            for (int i = 0; i < func.Params.Count; i++)
            {
                string p = func.Params[i];
                var t = paramTypes[i];
                _out.WriteLine($"  store {LlT(t)} %arg.{Sym(p)}, ptr %{SlotReg(p)}");
            }
            _out.WriteLine("  br label %body");
            _out.WriteLine("body:");
        }
        _blockOpen = true;

        foreach (var instr in func.Body)
            CompileInstruction(instr, func);

        if (_blockOpen)
        {
            if (func.IsNaked) _out.WriteLine("  unreachable");
            else if (func.ReturnType == DataType.VOID) _out.WriteLine("  ret void");
            else _out.WriteLine($"  ret {LlT(func.ReturnType)} 0");
        }
        _out.WriteLine("}");
    }

    private void CompileInstruction(Instruction instr, Function func)
    {
        switch (instr)
        {
            case DebugLine dl:
                _out.WriteLine($"  ; line {dl.Line}: {dl.Text.Trim()}");
                return;

            case InlineExpansionMarker:
                return;   // codegen marker only; no LLVM output

            case Label lbl:
                if (_blockOpen) _out.WriteLine($"  br label %{BlockLabel(lbl.Name)}");
                _out.WriteLine($"{BlockLabel(lbl.Name)}:");
                _blockOpen = true;
                return;
        }

        EnsureBlock();

        switch (instr)
        {
            case Copy c:        StoreI32(LoadI32(c.Src), c.Dst); break;
            case Bitcast bc:    StoreI32(LoadI32(bc.Src), bc.Dst); break;
            case Unary u:       CompileUnary(u); break;
            case Binary b:      CompileBinary(b); break;
            case AugAssign aa:  CompileAug(aa); break;

            case BitSet bs:     CompileBitSet(bs.Target, bs.Bit); break;
            case BitClear bc2:  CompileBitClear(bc2.Target, bc2.Bit); break;
            case BitWrite bw:   CompileBitWrite(bw); break;
            case BitCheck bk:   CompileBitCheck(bk); break;

            case LoadIndirect li:  CompileLoadIndirect(li); break;
            case StoreIndirect si: CompileStoreIndirect(si); break;
            case ArrayLoad al:     CompileArrayLoad(al); break;
            case ArrayStore ast:   CompileArrayStore(ast); break;
            case BytearrayLoad bl:  CompileBytearrayLoad(bl); break;
            case BytearrayStore bs: CompileBytearrayStore(bs); break;

            case Jump j:
                _out.WriteLine($"  br label %{BlockLabel(j.Target)}");
                _blockOpen = false;
                break;

            case JumpIfZero jz:           CondJump(IcmpZero(LoadI32(jz.Condition), "eq"), jz.Target); break;
            case JumpIfNotZero jnz:        CondJump(IcmpZero(LoadI32(jnz.Condition), "ne"), jnz.Target); break;
            case JumpIfEqual je:           CondJump(IcmpRel("eq", je.Src1, je.Src2), je.Target); break;
            case JumpIfNotEqual jne:       CondJump(IcmpRel("ne", jne.Src1, jne.Src2), jne.Target); break;
            case JumpIfLessThan jl:        CondJump(IcmpRel("lt", jl.Src1, jl.Src2), jl.Target); break;
            case JumpIfLessOrEqual jle:    CondJump(IcmpRel("le", jle.Src1, jle.Src2), jle.Target); break;
            case JumpIfGreaterThan jg:     CondJump(IcmpRel("gt", jg.Src1, jg.Src2), jg.Target); break;
            case JumpIfGreaterOrEqual jge: CondJump(IcmpRel("ge", jge.Src1, jge.Src2), jge.Target); break;
            case JumpIfBitSet jbs:         CondJump(BitTest(jbs.Source, jbs.Bit, true), jbs.Target); break;
            case JumpIfBitClear jbc:       CondJump(BitTest(jbc.Source, jbc.Bit, false), jbc.Target); break;

            case Call call:     CompileCall(call); break;
            case Return r:      CompileReturn(r, func); break;

            case InlineAsm ia:  CompileInlineAsm(ia); break;

            default:
                throw new NotSupportedException(
                    $"RP2040 LLVM backend: IR instruction '{instr.GetType().Name}' is not supported yet " +
                    "(MVP covers GPIO/UART: copy, arithmetic, bit ops, control flow, direct calls).");
        }
    }

    // ── Operand load / store (everything flows through i32) ───────────────────

    // Returns an operand string that is logically an i32 value (a literal or %reg).
    private string LoadI32(Val v)
    {
        switch (v)
        {
            case Constant c:   return c.Value.ToString();
            case NoneVal:      return "0";
            case Variable var: return WidenToI32(EmitLoad(SlotPtr(var.Name), var.Type), var.Type);
            case Temporary t:  return WidenToI32(EmitLoad(SlotPtr(t.Name), t.Type), t.Type);
            case MemoryAddress m:
            {
                string p = Fresh();
                _out.WriteLine($"  {p} = inttoptr i32 {m.Address} to ptr");
                string raw = Fresh();
                _out.WriteLine($"  {raw} = load volatile {LlT(m.Type)}, ptr {p}");
                return WidenToI32(raw, m.Type);
            }
            case ArrayBase ab:
            {
                // Base address of a named array as an integer (e.g. passed as a ptr).
                string r = Fresh();
                _out.WriteLine($"  {r} = ptrtoint ptr @{Sym(ab.ArrayName)} to i32");
                return r;
            }
            case FunctionRef fr:
            {
                // Address of a function as an integer, with the Thumb bit set (bit 0)
                // so it is a valid Cortex-M call/branch target -- e.g. a task entry
                // written into a context-switch stack frame's PC slot.
                string pi = Fresh();
                _out.WriteLine($"  {pi} = ptrtoint ptr @{Sym(fr.FunctionName)} to i32");
                string thumb = Fresh();
                _out.WriteLine($"  {thumb} = or i32 {pi}, 1");
                return thumb;
            }
            default:
                throw new NotSupportedException(
                    $"RP2040 LLVM backend: operand '{v.GetType().Name}' cannot be read yet.");
        }
    }

    private void StoreI32(string i32val, Val dst)
    {
        switch (dst)
        {
            case Variable var: EmitStore(NarrowFromI32(i32val, var.Type), var.Type, SlotPtr(var.Name)); break;
            case Temporary t:  EmitStore(NarrowFromI32(i32val, t.Type), t.Type, SlotPtr(t.Name)); break;
            case MemoryAddress m:
            {
                string narrow = NarrowFromI32(i32val, m.Type);
                string p = Fresh();
                _out.WriteLine($"  {p} = inttoptr i32 {m.Address} to ptr");
                _out.WriteLine($"  store volatile {LlT(m.Type)} {narrow}, ptr {p}");
                break;
            }
            default:
                throw new NotSupportedException(
                    $"RP2040 LLVM backend: operand '{dst.GetType().Name}' cannot be written yet.");
        }
    }

    private string EmitLoad(string ptr, DataType t)
    {
        string r = Fresh();
        _out.WriteLine($"  {r} = load {LlT(t)}, ptr {ptr}");
        return r;
    }

    private void EmitStore(string val, DataType t, string ptr)
        => _out.WriteLine($"  store {LlT(t)} {val}, ptr {ptr}");

    // Sign/zero-extend a narrower load result to i32 (no-op for i32-wide values).
    private string WidenToI32(string val, DataType t)
    {
        if (LlT(t) == "i32") return val;
        string r = Fresh();
        string op = t.IsSigned() ? "sext" : "zext";
        _out.WriteLine($"  {r} = {op} {LlT(t)} {val} to i32");
        return r;
    }

    // Truncate an i32 working value to a narrower slot/register width.
    private string NarrowFromI32(string val, DataType t)
    {
        if (LlT(t) == "i32") return val;
        string r = Fresh();
        _out.WriteLine($"  {r} = trunc i32 {val} to {LlT(t)}");
        return r;
    }

    private string SlotPtr(string name)
        => _globals.Contains(name) ? $"@{Sym(name)}" : $"%{SlotReg(name)}";

    // ── Arithmetic / logic ───────────────────────────────────────────────────

    private void CompileUnary(Unary u)
    {
        string x = LoadI32(u.Src);
        string r = Fresh();
        switch (u.Op)
        {
            case UnaryOp.Neg:    _out.WriteLine($"  {r} = sub i32 0, {x}"); break;
            case UnaryOp.BitNot: _out.WriteLine($"  {r} = xor i32 {x}, -1"); break;
            case UnaryOp.Not:
                string c = Fresh();
                _out.WriteLine($"  {c} = icmp eq i32 {x}, 0");
                _out.WriteLine($"  {r} = zext i1 {c} to i32");
                break;
            default: throw new NotSupportedException($"unary op {u.Op}");
        }
        StoreI32(r, u.Dst);
    }

    private void CompileBinary(Binary b)
    {
        string a = LoadI32(b.Src1);
        string c = LoadI32(b.Src2);
        bool signed = IsSigned(b.Src1) || IsSigned(b.Src2);
        string r = EmitBinOp(b.Op, a, c, signed);
        StoreI32(r, b.Dst);
    }

    private void CompileAug(AugAssign aa)
    {
        string cur = LoadI32(aa.Target);
        string operand = LoadI32(aa.Operand);
        bool signed = IsSigned(aa.Target) || IsSigned(aa.Operand);
        string r = EmitBinOp(aa.Op, cur, operand, signed);
        StoreI32(r, aa.Target);
    }

    private string EmitBinOp(BinaryOp op, string a, string b, bool signed)
    {
        string r = Fresh();
        switch (op)
        {
            case BinaryOp.Add:      _out.WriteLine($"  {r} = add i32 {a}, {b}"); break;
            case BinaryOp.Sub:      _out.WriteLine($"  {r} = sub i32 {a}, {b}"); break;
            case BinaryOp.Mul:      _out.WriteLine($"  {r} = mul i32 {a}, {b}"); break;
            case BinaryOp.Div:      _out.WriteLine($"  {r} = {(signed ? "sdiv" : "udiv")} i32 {a}, {b}"); break;
            case BinaryOp.FloorDiv: _out.WriteLine($"  {r} = {(signed ? "sdiv" : "udiv")} i32 {a}, {b}"); break;
            case BinaryOp.Mod:      _out.WriteLine($"  {r} = {(signed ? "srem" : "urem")} i32 {a}, {b}"); break;
            case BinaryOp.BitAnd:   _out.WriteLine($"  {r} = and i32 {a}, {b}"); break;
            case BinaryOp.BitOr:    _out.WriteLine($"  {r} = or i32 {a}, {b}"); break;
            case BinaryOp.BitXor:   _out.WriteLine($"  {r} = xor i32 {a}, {b}"); break;
            case BinaryOp.LShift:   _out.WriteLine($"  {r} = shl i32 {a}, {b}"); break;
            case BinaryOp.RShift:   _out.WriteLine($"  {r} = {(signed ? "ashr" : "lshr")} i32 {a}, {b}"); break;
            case BinaryOp.Equal:        return ZextCmp("eq", a, b, signed);
            case BinaryOp.NotEqual:     return ZextCmp("ne", a, b, signed);
            case BinaryOp.LessThan:     return ZextCmp("lt", a, b, signed);
            case BinaryOp.LessEqual:    return ZextCmp("le", a, b, signed);
            case BinaryOp.GreaterThan:  return ZextCmp("gt", a, b, signed);
            case BinaryOp.GreaterEqual: return ZextCmp("ge", a, b, signed);
            default: throw new NotSupportedException($"binary op {op}");
        }
        return r;
    }

    private string ZextCmp(string basePred, string a, string b, bool signed)
    {
        string c = Fresh();
        _out.WriteLine($"  {c} = icmp {Predicate(basePred, signed)} i32 {a}, {b}");
        string r = Fresh();
        _out.WriteLine($"  {r} = zext i1 {c} to i32");
        return r;
    }

    // ── Bit operations ───────────────────────────────────────────────────────

    private void CompileBitSet(Val target, int bit)
    {
        string cur = LoadI32(target);
        string r = Fresh();
        _out.WriteLine($"  {r} = or i32 {cur}, {1 << bit}");
        StoreI32(r, target);
    }

    private void CompileBitClear(Val target, int bit)
    {
        string cur = LoadI32(target);
        string r = Fresh();
        _out.WriteLine($"  {r} = and i32 {cur}, {~(1 << bit)}");
        StoreI32(r, target);
    }

    private void CompileBitWrite(BitWrite bw)
    {
        string src = LoadI32(bw.Src);
        string bitVal = Fresh();
        _out.WriteLine($"  {bitVal} = and i32 {src}, 1");
        string shifted = Fresh();
        _out.WriteLine($"  {shifted} = shl i32 {bitVal}, {bw.Bit}");
        string cur = LoadI32(bw.Target);
        string cleared = Fresh();
        _out.WriteLine($"  {cleared} = and i32 {cur}, {~(1 << bw.Bit)}");
        string r = Fresh();
        _out.WriteLine($"  {r} = or i32 {cleared}, {shifted}");
        StoreI32(r, bw.Target);
    }

    private void CompileBitCheck(BitCheck bk)
    {
        string src = LoadI32(bk.Source);
        string sh = Fresh();
        _out.WriteLine($"  {sh} = lshr i32 {src}, {bk.Bit}");
        string r = Fresh();
        _out.WriteLine($"  {r} = and i32 {sh}, 1");
        StoreI32(r, bk.Dst);
    }

    // Returns an i1 SSA value: (source & (1<<bit)) != 0  (set==true) or == 0 (set==false).
    private string BitTest(Val source, int bit, bool set)
    {
        string src = LoadI32(source);
        string masked = Fresh();
        _out.WriteLine($"  {masked} = and i32 {src}, {1 << bit}");
        string c = Fresh();
        _out.WriteLine($"  {c} = icmp {(set ? "ne" : "eq")} i32 {masked}, 0");
        return c;
    }

    // ── Pointer dereference ──────────────────────────────────────────────────

    private void CompileLoadIndirect(LoadIndirect li)
    {
        // Access width is the pointer's element type when known (li.Elem); fall
        // back to the destination's type for legacy IR that left it UINT8.
        DataType t = li.Elem != DataType.UINT8 ? li.Elem : ValType(li.Dst);
        string addr = LoadI32(li.SrcPtr);
        string p = Fresh();
        _out.WriteLine($"  {p} = inttoptr i32 {addr} to ptr");
        string raw = Fresh();
        // VOLATILE: a ptr access targets MMIO or shared state -- the optimizer must
        // not cache, reorder or eliminate it (else e.g. an RTOS queue index read
        // back after a write keeps a stale value).
        _out.WriteLine($"  {raw} = load volatile {LlT(t)}, ptr {p}");
        StoreI32(WidenToI32(raw, t), li.Dst);
    }

    private void CompileStoreIndirect(StoreIndirect si)
    {
        // Access width is the pointer's element type when known (si.Elem); fall
        // back to the source value's type for legacy IR that left it UINT8.
        DataType t = si.Elem != DataType.UINT8 ? si.Elem : ValType(si.Src);
        string val = NarrowFromI32(LoadI32(si.Src), t);
        string addr = LoadI32(si.DstPtr);
        string p = Fresh();
        _out.WriteLine($"  {p} = inttoptr i32 {addr} to ptr");
        _out.WriteLine($"  store volatile {LlT(t)} {val}, ptr {p}");
    }

    // ── Named arrays (ZCA instance backing stores, fixed buffers) ────────────
    private string ArrayElemPtr(string arrayName, Val index, int elemSize)
    {
        string idx = LoadI32(index);
        string byteOff;
        if (elemSize == 1)
            byteOff = idx;
        else
        {
            byteOff = Fresh();
            _out.WriteLine($"  {byteOff} = mul i32 {idx}, {elemSize}");
        }
        string p = Fresh();
        _out.WriteLine($"  {p} = getelementptr inbounds i8, ptr @{Sym(arrayName)}, i32 {byteOff}");
        return p;
    }

    private void CompileArrayLoad(ArrayLoad al)
    {
        string p = ArrayElemPtr(al.ArrayName, al.Index, al.ElemType.SizeOf());
        string raw = Fresh();
        _out.WriteLine($"  {raw} = load volatile {LlT(al.ElemType)}, ptr {p}");
        StoreI32(WidenToI32(raw, al.ElemType), al.Dst);
    }

    private void CompileArrayStore(ArrayStore ast)
    {
        string val = NarrowFromI32(LoadI32(ast.Src), ast.ElemType);
        string p = ArrayElemPtr(ast.ArrayName, ast.Index, ast.ElemType.SizeOf());
        _out.WriteLine($"  store volatile {LlT(ast.ElemType)} {val}, ptr {p}");
    }

    // ── Bytearrays / ZCA instance fields (byte access through a held pointer) ──
    private string BytearrayElemPtr(string ptrName, Val index)
    {
        string addr = LoadI32(new Variable(ptrName, DataType.UINT32));
        string idx = LoadI32(index);
        string basep = Fresh();
        _out.WriteLine($"  {basep} = inttoptr i32 {addr} to ptr");
        string p = Fresh();
        _out.WriteLine($"  {p} = getelementptr inbounds i8, ptr {basep}, i32 {idx}");
        return p;
    }

    private void CompileBytearrayLoad(BytearrayLoad bl)
    {
        string p = BytearrayElemPtr(bl.PtrName, bl.Index);
        string raw = Fresh();
        _out.WriteLine($"  {raw} = load volatile i8, ptr {p}");
        StoreI32(WidenToI32(raw, DataType.UINT8), bl.Dst);
    }

    private void CompileBytearrayStore(BytearrayStore bs)
    {
        string val = NarrowFromI32(LoadI32(bs.Src), DataType.UINT8);
        string p = BytearrayElemPtr(bs.PtrName, bs.Index);
        _out.WriteLine($"  store volatile i8 {val}, ptr {p}");
    }

    // ── Calls / return / control flow ────────────────────────────────────────

    private void CompileCall(Call call)
    {
        string callee = call.FunctionName;
        DataType ret = _returnTypes.TryGetValue(callee, out var rt) ? rt : DataType.VOID;
        var ptypes = _paramTypes.TryGetValue(callee, out var pl) ? pl : null;

        var args = new List<string>();
        for (int i = 0; i < call.Args.Count; i++)
        {
            DataType at = ptypes != null && i < ptypes.Count ? ptypes[i] : DataType.UINT32;
            string v = NarrowFromI32(LoadI32(call.Args[i]), at);
            args.Add($"{LlT(at)} {v}");
        }
        string argList = string.Join(", ", args);

        // A @naked function returns via `bx lr`. If the optimizer tail-calls it
        // (`b` instead of `bl`), LR is never set to the post-call address -- and when
        // such a function pends a context switch (RTOS yield), the task resumes with a
        // STALE LR and re-executes the caller's prior code. Force a real call (`bl`)
        // by marking the call `notail`.
        string tailMod = _nakedFns.Contains(callee) ? "notail " : "";

        if (ret == DataType.VOID)
        {
            _out.WriteLine($"  {tailMod}call void @{Sym(callee)}({argList})");
        }
        else
        {
            string r = Fresh();
            _out.WriteLine($"  {r} = {tailMod}call {LlT(ret)} @{Sym(callee)}({argList})");
            if (call.Dst is not NoneVal)
                StoreI32(WidenToI32(r, ret), call.Dst);
        }
    }

    private void CompileReturn(Return r, Function func)
    {
        if (func.ReturnType == DataType.VOID || r.Value is NoneVal)
        {
            _out.WriteLine("  ret void");
        }
        else
        {
            string v = NarrowFromI32(LoadI32(r.Value), func.ReturnType);
            _out.WriteLine($"  ret {LlT(func.ReturnType)} {v}");
        }
        _blockOpen = false;
    }

    private void CompileInlineAsm(InlineAsm ia)
    {
        if (ia.Operands is { Count: > 0 })
            throw new NotSupportedException(
                "RP2040 LLVM backend: operand-form inline asm (asm(\"...\", a, b)) is not supported yet.");
        // No-operand asm: emit as a side-effecting barrier carrying the raw text.
        string escaped = ia.Code.Replace("\\", "\\\\").Replace("\"", "\\22");
        _out.WriteLine($"  call void asm sideeffect \"{escaped}\", \"\"()");
    }

    // Emit `br i1 <cond>, label %target, label %fallthrough` and open the
    // fall-through block so subsequent (false-edge) instructions land there.
    private void CondJump(string i1Cond, string target)
    {
        string ft = $"ft.{_ssa++}";
        _out.WriteLine($"  br i1 {i1Cond}, label %{BlockLabel(target)}, label %{ft}");
        _out.WriteLine($"{ft}:");
        _blockOpen = true;
    }

    private string IcmpZero(string val, string pred)
    {
        string c = Fresh();
        _out.WriteLine($"  {c} = icmp {pred} i32 {val}, 0");
        return c;
    }

    private string IcmpRel(string basePred, Val s1, Val s2)
    {
        string a = LoadI32(s1);
        string b = LoadI32(s2);
        bool signed = IsSigned(s1) || IsSigned(s2);
        string c = Fresh();
        _out.WriteLine($"  {c} = icmp {Predicate(basePred, signed)} i32 {a}, {b}");
        return c;
    }

    // If the current block was closed by a terminator and more instructions
    // follow with no label, open a fresh (unreachable) block to keep IR valid.
    private void EnsureBlock()
    {
        if (_blockOpen) return;
        _out.WriteLine($"dead.{_ssa++}:");
        _blockOpen = true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string Fresh() => $"%{_ssa++}";

    private static string Predicate(string basePred, bool signed) => basePred switch
    {
        "eq" => "eq",
        "ne" => "ne",
        "lt" => signed ? "slt" : "ult",
        "le" => signed ? "sle" : "ule",
        "gt" => signed ? "sgt" : "ugt",
        "ge" => signed ? "sge" : "uge",
        _ => throw new NotSupportedException($"predicate {basePred}")
    };

    private static bool IsSigned(Val v) => v switch
    {
        Variable var => var.Type.IsSigned(),
        Temporary t  => t.Type.IsSigned(),
        MemoryAddress m => m.Type.IsSigned(),
        _ => false
    };

    private static DataType ValType(Val v) => v switch
    {
        Variable var => var.Type,
        Temporary t  => t.Type,
        MemoryAddress m => m.Type,
        _ => DataType.UINT8
    };

    // Build the slot table (named Variable / Temporary -> declared type) for a
    // function by scanning its params and body.
    private Dictionary<string, DataType> CollectSlots(Function func)
    {
        var slots = new Dictionary<string, DataType>();
        void Note(Val? v)
        {
            switch (v)
            {
                case Variable var when !_globals.Contains(var.Name): slots[var.Name] = var.Type; break;
                case Temporary t: slots[t.Name] = t.Type; break;
            }
        }
        foreach (var instr in func.Body)
            foreach (var v in OperandsOf(instr)) Note(v);
        // Ensure every param has a slot even if it is never referenced.
        var ptypes = _paramTypes[func.Name];
        for (int i = 0; i < func.Params.Count; i++)
            if (!_globals.Contains(func.Params[i]))
                slots[func.Params[i]] = ptypes[i];
        return slots;
    }

    // Derive each parameter's DataType from the first body reference of that name.
    private static List<DataType> InferParamTypes(Function func)
    {
        var byName = new Dictionary<string, DataType>();
        foreach (var instr in func.Body)
            foreach (var v in OperandsOf(instr))
            {
                if (v is Variable var && !byName.ContainsKey(var.Name)) byName[var.Name] = var.Type;
                else if (v is Temporary t && !byName.ContainsKey(t.Name)) byName[t.Name] = t.Type;
            }
        return func.Params.Select(p => byName.TryGetValue(p, out var dt) ? dt : DataType.UINT32).ToList();
    }

    // Enumerate the Val operands of an instruction (for slot/type discovery).
    private static IEnumerable<Val> OperandsOf(Instruction instr)
    {
        switch (instr)
        {
            case Return r: yield return r.Value; break;
            case Unary u: yield return u.Src; yield return u.Dst; break;
            case Binary b: yield return b.Src1; yield return b.Src2; yield return b.Dst; break;
            case Copy c: yield return c.Src; yield return c.Dst; break;
            case Bitcast bc: yield return bc.Src; yield return bc.Dst; break;
            case LoadIndirect li: yield return li.SrcPtr; yield return li.Dst; break;
            case StoreIndirect si: yield return si.Src; yield return si.DstPtr; break;
            case ArrayLoad al: yield return al.Index; yield return al.Dst; break;
            case ArrayStore ast: yield return ast.Index; yield return ast.Src; break;
            case BytearrayLoad bl: yield return bl.Index; yield return bl.Dst; break;
            case BytearrayStore bs: yield return bs.Index; yield return bs.Src; break;
            case JumpIfZero jz: yield return jz.Condition; break;
            case JumpIfNotZero jnz: yield return jnz.Condition; break;
            case JumpIfEqual je: yield return je.Src1; yield return je.Src2; break;
            case JumpIfNotEqual jne: yield return jne.Src1; yield return jne.Src2; break;
            case JumpIfLessThan jl: yield return jl.Src1; yield return jl.Src2; break;
            case JumpIfLessOrEqual jle: yield return jle.Src1; yield return jle.Src2; break;
            case JumpIfGreaterThan jg: yield return jg.Src1; yield return jg.Src2; break;
            case JumpIfGreaterOrEqual jge: yield return jge.Src1; yield return jge.Src2; break;
            case BitSet bs: yield return bs.Target; break;
            case BitClear bc2: yield return bc2.Target; break;
            case BitWrite bw: yield return bw.Target; yield return bw.Src; break;
            case BitCheck bk: yield return bk.Source; yield return bk.Dst; break;
            case JumpIfBitSet jbs: yield return jbs.Source; break;
            case JumpIfBitClear jbc: yield return jbc.Source; break;
            case AugAssign aa: yield return aa.Target; yield return aa.Operand; break;
            case Call call: foreach (var a in call.Args) yield return a; yield return call.Dst; break;
            case InlineAsm ia: if (ia.Operands != null) foreach (var a in ia.Operands) yield return a; break;
        }
    }

    private static string LlT(DataType t) => t switch
    {
        DataType.UINT8 or DataType.INT8   => "i8",
        DataType.UINT16 or DataType.INT16 => "i16",
        DataType.UINT32 or DataType.INT32 => "i32",
        DataType.FUNCREF or DataType.GC_REF => "i32",
        DataType.VOID => "void",
        DataType.UNKNOWN => "i8",
        DataType.FLOAT => throw new NotSupportedException(
            "RP2040 LLVM backend: floating-point is not supported yet."),
        _ => "i8"
    };

    // LLVM block label derived from a PyMCU label name.
    private static string BlockLabel(string name) => "L." + Sym(name);

    // LLVM local slot register name for a variable/temporary IR name.
    private static string SlotReg(string name) => "v." + Sym(name);

    // Sanitize an IR symbol into a valid LLVM identifier body.
    // Emitted symbol name. @export_c/@used functions keep their ORIGINAL (unmangled)
    // name so inline asm and external C can reach them by their source name --
    // `asm("bl _schedule")` resolves even when the function lives in a module whose
    // calls would otherwise be prefixed (e.g. freertos__schedule).
    private static string EmitSym(Function f) =>
        Sym(f.IsExportC && !string.IsNullOrEmpty(f.OriginalName) ? f.OriginalName! : f.Name);

    private static string Sym(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            bool ok = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';
            sb.Append(ok ? ch : '_');
        }
        return sb.ToString();
    }

    // RP2040 context/interrupt handling is generated by LLVM + the crt0 runtime,
    // not here. These satisfy the CodeGen contract shared with the asm backends.
    public override void EmitContextSave() { }
    public override void EmitContextRestore() { }
    public override void EmitInterruptReturn() { }
}
