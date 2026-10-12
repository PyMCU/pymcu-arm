using RP2040.Peripherals.Gpio;
using RP2040.TestKit;
using RP2040.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.RP2040;

/// <summary>
/// A DS18B20 on a 1-Wire bus, answering the master cycle by cycle (the RP2040 twin of the AVR simulator).
///
/// The bus is open drain with a pull-up: the master pulls the line low by making the pin an
/// output driven low and lets go by making it an input again, and the slave pulls it low through
/// the pin's external input value. So the line is low exactly when either side holds it, and the
/// simulator watches the master's output enable and level one instruction at a time.
///
/// What it models, with the timing the protocol gives it:
///   - reset: a low of 480 us or more; the slave answers with a presence pulse 30 us after the
///     release, 120 us long;
///   - a write slot from the master: low about 6 us is a 1, low about 60 us is a 0, decided when
///     the master lets go;
///   - a read slot: the master pulls low about 3 us and samples about 12 us after the fall; the
///     slave holds the line low for a 0 until 25 us after the fall;
///   - ROM commands SEARCH (F0), READ ROM (33), SKIP ROM (CC), MATCH ROM (55);
///   - function commands CONVERT T (44, done at once), READ SCRATCHPAD (BE), WRITE SCRATCHPAD (4E).
/// </summary>
internal sealed class OneWireSlaveSimulator
{
    private const int CyclesPerUs = 125;    // 125 MHz

    private readonly RP2040TestSimulation _sim;
    private readonly GpioPin _gpio;
    private long _cycles;

    public byte[] Rom { get; }
    public byte[] Scratchpad { get; } = new byte[9];

    /// <summary>The command bytes the master sent after each reset, for the assertions.</summary>
    public List<byte> CommandsSeen { get; } = new();


    /// <summary>Every byte the master wrote, in order, whatever the slave made of it.</summary>
    public List<byte> BytesWritten { get; } = new();

    /// <summary>Microseconds from the master's release to the start of the presence pulse.</summary>
    public int PresenceDelayUs { get; set; } = 30;

    /// <summary>Length of the presence pulse in microseconds.</summary>
    public int PresenceLengthUs { get; set; } = 120;

    private enum Mode { Idle, Recv, Send }
    private enum Phase { RomCommand, MatchRom, Search, FunctionCommand, WriteScratch, Done }

    private Mode _mode = Mode.Idle;
    private Phase _phase = Phase.Done;
    private bool _wasLow;
    private long _fall;
    private long _presenceStart = -1, _presenceEnd = -1, _releaseAt = -1;
    private bool _slaveLow;

    private int _recvBits, _recvValue, _recvBytes;
    private readonly byte[] _matchBuf = new byte[8];
    private readonly Queue<bool> _sendBits = new();
    private int _searchIndex, _searchStep;
    private bool _sendFinished;

    /// <param name="sim">The RP2040 simulation running the firmware.</param>
    /// <param name="gpio">The data pin.</param>
    /// <param name="serial">The six serial-number bytes; the family code is 0x28 and the CRC is computed.</param>
    /// <param name="rawTemperature">The 12-bit reading, 1/16 degree per count.</param>
    public OneWireSlaveSimulator(RP2040TestSimulation sim, GpioPin gpio, byte[] serial, short rawTemperature)
    {
        _sim = sim; _gpio = gpio;
        Rom = new byte[8];
        Rom[0] = 0x28;
        Array.Copy(serial, 0, Rom, 1, 6);
        Rom[7] = Crc8(Rom, 7);
        Scratchpad[0] = (byte)(rawTemperature & 0xFF);
        Scratchpad[1] = (byte)((rawTemperature >> 8) & 0xFF);
        Scratchpad[2] = 0x4B;                   // TH
        Scratchpad[3] = 0x46;                   // TL
        Scratchpad[4] = 0x7F;                   // configuration: 12 bits
        Scratchpad[5] = 0xFF;
        Scratchpad[6] = 0x0C;
        Scratchpad[7] = 0x10;
        Scratchpad[8] = Crc8(Scratchpad, 8);
        _gpio.ForceInput(true);
    }

    /// <summary>The Dallas/Maxim CRC-8 (polynomial 0x8C reflected) over the first n bytes.</summary>
    public static byte Crc8(byte[] data, int n)
    {
        byte crc = 0;
        for (int i = 0; i < n; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (byte)((crc & 1) != 0 ? (crc >> 1) ^ 0x8C : crc >> 1);
        }
        return crc;
    }

    private bool MasterLow => _gpio.IsOutput && !_gpio.OutputValue;

    /// <summary>Runs the firmware one instruction and lets the slave react to the line.</summary>
    public void Step()
    {
        _sim.RunInstructions(1);
        _cycles += Math.Max(1, _sim.Rp2040.LastElapsedCycles);
        long now = _cycles;

        bool low = MasterLow;
        if (low && !_wasLow) OnFall(now);
        else if (!low && _wasLow) OnRise(now);
        _wasLow = low;

        if (_presenceStart >= 0 && now >= _presenceStart) { Pull(true); _presenceStart = -1; }
        if (_presenceEnd >= 0 && now >= _presenceEnd) { Pull(false); _presenceEnd = -1; }
        if (_releaseAt >= 0 && now >= _releaseAt) { Pull(false); _releaseAt = -1; }
    }

    /// <summary>Steps until the predicate holds or the simulated time runs out.</summary>
    public bool RunUntil(Func<bool> done, double maxMs)
    {
        long limit = _cycles + (long)(maxMs * 1000 * CyclesPerUs);
        while (_cycles < limit)
        {
            if (done()) return true;
            Step();
        }
        return done();
    }

    private void Pull(bool low)
    {
        _slaveLow = low;
        _gpio.ForceInput(!low);
    }

    private void OnFall(long now)
    {
        _fall = now;
        if (_mode != Mode.Send) return;
        // A read slot: the next bit goes out now. A 0 holds the line low until 25 us after the fall.
        bool bit = _sendBits.Count > 0 ? _sendBits.Dequeue() : true;
        if (!bit)
        {
            Pull(true);
            _releaseAt = now + 25 * CyclesPerUs;
        }
        // The mode changes when the master lets go of this last slot, not at its fall: the rise of a
        // read slot must not be taken for the end of a write slot.
        if (_sendBits.Count == 0) _sendFinished = true;
    }

    private void OnRise(long now)
    {
        long low = now - _fall;
        if (_sendFinished && low < 400 * CyclesPerUs)
        {
            _sendFinished = false;
            AfterSend();
            return;
        }
        if (low >= 400 * CyclesPerUs)
        {
            // A reset: forget the transaction and answer with a presence pulse.
            _presenceStart = now + PresenceDelayUs * CyclesPerUs;
            _presenceEnd = _presenceStart + PresenceLengthUs * CyclesPerUs;
            _mode = Mode.Recv; _phase = Phase.RomCommand;
            _recvBits = 0; _recvValue = 0; _recvBytes = 0;
            _sendBits.Clear();
            _sendFinished = false;
            return;
        }
        if (_mode != Mode.Recv) return;
        bool bit = low < 30 * CyclesPerUs;
        _recvValue |= (bit ? 1 : 0) << _recvBits;
        if (++_recvBits < 8 && !(_phase == Phase.Search && _searchStep == 2)) return;
        int value = _recvValue;
        int bits = _recvBits;
        _recvBits = 0; _recvValue = 0;
        OnReceived(value, bits);
    }

    private void SendByteLsbFirst(byte b)
    {
        for (int i = 0; i < 8; i++) _sendBits.Enqueue(((b >> i) & 1) == 1);
    }

    private void OnReceived(int value, int bits)
    {
        if (bits == 8) BytesWritten.Add((byte)value);
        switch (_phase)
        {
            case Phase.RomCommand:
                CommandsSeen.Add((byte)value);
                switch (value)
                {
                    case 0xF0:
                        _phase = Phase.Search; _searchIndex = 0; _searchStep = 0;
                        QueueSearchBits();
                        break;
                    case 0x33:
                        foreach (var b in Rom) SendByteLsbFirst(b);
                        _mode = Mode.Send; _phase = Phase.Done;
                        break;
                    case 0xCC: _phase = Phase.FunctionCommand; break;
                    case 0x55: _phase = Phase.MatchRom; _recvBytes = 0; break;
                    default: _mode = Mode.Idle; _phase = Phase.Done; break;
                }
                break;
            case Phase.MatchRom:
                _matchBuf[_recvBytes++] = (byte)value;
                if (_recvBytes == 8)
                {
                    bool match = _matchBuf.SequenceEqual(Rom);
                    if (match) _phase = Phase.FunctionCommand;
                    else { _mode = Mode.Idle; _phase = Phase.Done; }
                }
                break;
            case Phase.Search:
                // The master's choice for this bit: 1 bit received. A slave whose bit differs drops out.
                bool romBit = RomBit(_searchIndex);
                if ((value & 1) == 1 != romBit) { _mode = Mode.Idle; _phase = Phase.Done; break; }
                if (++_searchIndex == 64) { _mode = Mode.Idle; _phase = Phase.Done; break; }
                QueueSearchBits();
                break;
            case Phase.FunctionCommand:
                CommandsSeen.Add((byte)value);
                switch (value)
                {
                    case 0x44:
                        // The conversion is already done: the master's polling reads see 1s.
                        _mode = Mode.Send; _phase = Phase.Done;
                        break;
                    case 0xBE:
                        foreach (var b in Scratchpad) SendByteLsbFirst(b);
                        _mode = Mode.Send; _phase = Phase.Done;
                        break;
                    case 0x4E: _phase = Phase.WriteScratch; _recvBytes = 0; break;
                    default: _mode = Mode.Idle; _phase = Phase.Done; break;
                }
                break;
            case Phase.WriteScratch:
                Scratchpad[2 + _recvBytes++] = (byte)value;
                if (_recvBytes == 3)
                {
                    Scratchpad[8] = Crc8(Scratchpad, 8);
                    _mode = Mode.Idle; _phase = Phase.Done;
                }
                break;
        }
    }

    private bool RomBit(int index) => ((Rom[index / 8] >> (index % 8)) & 1) == 1;

    private void QueueSearchBits()
    {
        bool b = RomBit(_searchIndex);
        _sendBits.Enqueue(b);
        _sendBits.Enqueue(!b);
        _mode = Mode.Send;
        _searchStep = 0;
    }

    private void AfterSend()
    {
        if (_phase == Phase.Search)
        {
            // Both bits are out: the next write slot carries the master's choice.
            _mode = Mode.Recv; _searchStep = 2; _recvBits = 0; _recvValue = 0;
        }
        else
        {
            _mode = Mode.Idle;
        }
    }
}
