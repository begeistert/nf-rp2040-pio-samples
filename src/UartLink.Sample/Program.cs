//
// Sample — a full duplex UART between two boards, both ends built from PIO state machines.
//
// This is the hardware counterpart to the emulator probes. It is symmetric: the same image runs on
// both boards, each one transmitting and receiving at the same time, so neither is "the reference".
// That is the whole point. A loopback on a single board shares the crystal, so a wrong clock
// divider makes the transmitter and the receiver wrong by the same amount and the test passes. Two
// boards have independent crystals: the bit period has to be right in absolute terms or the link
// does not come up.
//
// Run it on an RP2040 and an RP2350 and each one validates the other's divider maths on a different
// silicon at a different system clock. On a board with wireless it also proves Pio.MinIndex on real
// hardware, because PIO0 belongs to the radio there and only PIO1 is reachable.
//
// Wiring, and nothing else:
//
//     A.GND  --  B.GND        common reference; a level is only a level relative to one
//     A.GP2  ->  B.GP3
//     B.GP2  ->  A.GP3
//
// Transmitter, three instructions. Side-set drives the line, so the level is part of every
// instruction instead of a separate SET and the loop stays tight enough to hold the bit period:
//
//   .side_set 1 opt
//   .wrap_target
//     pull        side 1 [7]   ; wait for a byte, idle high, hold one bit time
//     set x, 7    side 0 [7]   ; start bit: low for one bit time
//   bitloop:
//     out pins, 1              ; shift the next data bit onto the line
//     jmp x--, bitloop   [6]   ; eight of them, one bit time each
//   .wrap                      ; wrap into the pull, which drives the stop bit
//
// Receiver. The delay after the start bit is 11 cycles, not 8: one cycle is spent in the WAIT that
// detected the falling edge, and the sample has to land in the MIDDLE of the first data bit, half a
// bit period further on. Sampling at the edge is what makes a link work at 115200 and fail at 1M.
//
//   .wrap_target
//   start:
//     wait 0 pin 0             ; falling edge of the start bit
//     set x, 7    [10]         ; eight bits, and slide to the centre of bit 0
//   bitloop:
//     in pins, 1               ; sample
//     jmp x--, bitloop   [6]   ; eight cycles per bit
//     jmp pin, good_stop       ; the stop bit must be high
//     wait 1 pin 0             ; framing error: drop the byte and resynchronise
//     jmp start
//   good_stop:
//     push
//   .wrap
//
// Both state machines are clocked at 8x the baud rate, so an instruction slot plus its delay is
// exactly one bit period.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace UartLinkSample
{
    /// <summary>Transmit half of the link: bytes in, 8N1 on a pin.</summary>
    public sealed class PioUartTx : IDisposable
    {
        private const int CyclesPerBit = 8;
        private const int WriteTimeoutMs = 1000;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioUartTx"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="txPin">The GPIO to drive.</param>
        /// <param name="baudRate">Bits per second.</param>
        /// <param name="sysClockHz">The system clock this board actually runs at.</param>
        public PioUartTx(PioBlock pio, int txPin, int baudRate, float sysClockHz)
        {
            _pio = pio;
            _program = BuildProgram();
            _offset = _pio.AddProgram(_program);
            _pio.InitGpio(txPin);
            _sm = _pio.ClaimStateMachine();

            _sm.SetConsecutivePinDirs(txPin, 1, true);

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .OutPins(txPin, 1)
                .SideSetPins(txPin)
                .ClockFromFrequency(baudRate * CyclesPerBit, sysClockHz));

            _sm.Enabled = true;
        }

        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
                SideSetOption = true,
            });

            asm.OutShift(ShiftDirection.Right, autoPull: false, threshold: 32);

            PioLabel bitloop = asm.DefineLabel();

            asm.WrapTarget();
            asm.Pull().Side(1).Delay(7);
            asm.Set(DestinationOperand.RegisterX, 7).Side(0).Delay(7);
            asm.MarkLabel(bitloop);
            asm.Out(DestinationOperand.Pins, 1);
            asm.Jmp(PioCondition.XPostDec, bitloop).Delay(6);
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Sends a block of bytes, one word per byte through the TX FIFO.</summary>
        /// <param name="data">The bytes to send.</param>
        /// <param name="count">How many of them.</param>
        /// <returns>The number of bytes the transfer actually moved.</returns>
        public int Send(byte[] data, int count)
        {
            uint[] words = new uint[count];
            for (int i = 0; i < count; i++)
            {
                words[i] = data[i];
            }

            return _sm.Write(words, 0, count, WriteTimeoutMs);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sm.Dispose();
            _pio.RemoveProgram(_program, _offset);
        }
    }

    /// <summary>Receive half of the link: 8N1 on a pin, bytes out.</summary>
    public sealed class PioUartRx : IDisposable
    {
        private const int CyclesPerBit = 8;
        internal const int FramingIrq = 1;
        internal const PioInterruptFlags FramingFlag = PioInterruptFlags.Sm1;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private readonly uint[] _words;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioUartRx"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="rxPin">The GPIO to sample.</param>
        /// <param name="baudRate">Bits per second.</param>
        /// <param name="sysClockHz">The system clock this board actually runs at.</param>
        public PioUartRx(PioBlock pio, int rxPin, int baudRate, float sysClockHz, int maxRun)
        {
            _words = new uint[maxRun];
            _pio = pio;
            _program = BuildProgram();
            _offset = _pio.AddProgram(_program);
            _pio.InitGpio(rxPin);
            _sm = _pio.ClaimStateMachine();

            _sm.SetConsecutivePinDirs(rxPin, 1, false);

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .InPins(rxPin)
                .JmpPin(rxPin)
                .ClockFromFrequency(baudRate * CyclesPerBit, sysClockHz));

            _sm.Enabled = true;
        }

        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler();

            asm.InShift(ShiftDirection.Right, autoPush: false, threshold: 32);

            PioLabel start = asm.DefineLabel();
            PioLabel bitloop = asm.DefineLabel();
            PioLabel goodStop = asm.DefineLabel();

            asm.WrapTarget();
            asm.MarkLabel(start);
            asm.WaitPin(false, 0);
            asm.Set(DestinationOperand.RegisterX, 7).Delay(10);
            asm.MarkLabel(bitloop);
            asm.In(SourceOperand.Pins, 1);
            asm.Jmp(PioCondition.XPostDec, bitloop).Delay(6);
            asm.Jmp(PioCondition.Pin, goodStop);
            asm.Irq(FramingIrq);
            asm.WaitPin(true, 0);
            asm.Jmp(start);
            asm.MarkLabel(goodStop);
            asm.Push();
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Gets a value indicating whether a byte is already waiting.</summary>
        public bool HasByte
        {
            get
            {
                return !_sm.IsRxEmpty;
            }
        }

        /// <summary>
        /// Blocks until a byte arrives. One native call per byte, so it cannot keep up with a fast
        /// line; it is here to resynchronise on the frame marker, not to carry the payload.
        /// </summary>
        /// <returns>The received byte.</returns>
        /// <exception cref="TimeoutException">Nothing arrived within the native wait budget.</exception>
        public byte Receive()
        {
            return (byte)(_sm.Get() >> 24);
        }

        /// <summary>
        /// Reads a run of bytes through a DMA channel paced by the RX FIFO's request line. The
        /// interpreter is out of the loop for the whole run, which is what makes the line rate the
        /// state machine's problem instead of the scheduler's.
        /// </summary>
        /// <param name="destination">Where the bytes land.</param>
        /// <param name="offset">First index to write.</param>
        /// <param name="count">How many bytes to read.</param>
        /// <param name="timeoutMs">How long to wait for the whole run.</param>
        /// <returns>The number of bytes that actually arrived, which is less than <paramref name="count"/> on a timeout.</returns>
        public int ReceiveMany(byte[] destination, int offset, int count, int timeoutMs)
        {
            int got = _sm.Read(_words, 0, count, timeoutMs);

            for (int i = 0; i < got; i++)
            {
                destination[offset + i] = (byte)(_words[i] >> 24);
            }

            return got;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sm.Dispose();
            _pio.RemoveProgram(_program, _offset);
        }
    }

    public class Program
    {
        private const int BaudRate = 57600;
        // NOT GP0/GP1: those are UART0, which is the Wire Protocol's transport on this target
        // (SERIAL_DRIVER SIOD0 in the target's serialcfg.h). Driving them from the PIO fights the
        // debugger for the same pads and neither side works.
        private const int TxPin = 2;
        private const int RxPin = 3;

        private const byte FrameMarker = 0x55;
        private const int PayloadBytes = 32;
        private const int FrameBytes = PayloadBytes + 3;
        // Diagnostic beacons. On this target the Wire Protocol carries Debug.WriteLine, and the only
        // part of it a plain terminal can pick out is the CLR's own exception trace -- which it emits
        // even for exceptions that are caught. So every outcome is reported by throwing from a
        // distinctly named method and catching it on the spot: the method name in the stack says what
        // happened. Set to false before this stops being a bring-up vehicle.
        private const bool Beacons = true;
        private const int BeaconEveryFrames = 200;

        private const int ReportEveryMs = 1000;
        private const int SendGapMs = 5;
        private const int FrameTimeoutMs = 200;

        // Set false to carry the payload with one native call per byte instead of a DMA run. Only
        // viable at a slow line rate, and it exists to tell a broken PIO receive program apart from a
        // broken DMA path: the two share every other part of the receiver.
        private const bool UseDmaReceive = true;

        private const bool StreamProbeMode = false;
        private const int StreamRun = 32;

        // Zero means transmit forever, which is what a hardware run wants: the receiver never starves,
        // so silence on the wire protocol means the link is healthy rather than merely finished. The
        // integration test sets a finite count so the run ends.
        private const int FramesToSend = 0;

        public static int FramesSent;
        public static int FramesOk;
        public static int FramesCorrupt;
        public static int FramesLost;
        public static int Timeouts;
        public static int TxDone;

        private static void BeaconAnyByte()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMarkerSeen()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconStreamMatch()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconStreamSkip()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconStreamGarbled()
        {
            throw new InvalidOperationException();
        }

        private static void StreamTransmit(PioUartTx tx)
        {
            byte[] run = new byte[StreamRun];
            byte next = 0;

            while (true)
            {
                for (int i = 0; i < StreamRun; i++)
                {
                    run[i] = next;
                    next++;
                }

                tx.Send(run, StreamRun);
                Thread.Sleep(SendGapMs);
            }
        }

        private static void StreamReceive(PioUartRx rx)
        {
            byte[] run = new byte[StreamRun];
            int expected = -1;
            int match = 0, skip = 0, garbled = 0;

            while (true)
            {
                if (rx.ReceiveMany(run, 0, StreamRun, FrameTimeoutMs) != StreamRun)
                {
                    expected = -1;
                    continue;
                }

                for (int i = 0; i < StreamRun; i++)
                {
                    byte got = run[i];

                    if (expected < 0)
                    {
                        expected = got;
                    }

                    if (got == (byte)expected)
                    {
                        match++;
                    }
                    else if (got == (byte)(expected + 1))
                    {
                        skip++;
                    }
                    else
                    {
                        garbled++;
                    }

                    expected = got + 1;
                }

                if (match >= 2000)
                {
                    match = 0;
                    try
                    {
                        BeaconStreamMatch();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                if (skip >= 20)
                {
                    skip = 0;
                    try
                    {
                        BeaconStreamSkip();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                if (garbled >= 20)
                {
                    garbled = 0;
                    try
                    {
                        BeaconStreamGarbled();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        private static void BeaconHealthy()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFrameCorrupt()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFrameLost()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconReceiveTimeout()
        {
            throw new InvalidOperationException();
        }

        private static void Fire(bool healthy, bool corrupt, bool lost, bool timeout)
        {
            if (!Beacons)
            {
                return;
            }

            try
            {
                if (healthy)
                {
                    BeaconHealthy();
                }
                else if (corrupt)
                {
                    BeaconFrameCorrupt();
                }
                else if (lost)
                {
                    BeaconFrameLost();
                }
                else if (timeout)
                {
                    BeaconReceiveTimeout();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static byte NextByte(ref uint state)
        {
            state = (state * 1664525u) + 1013904223u;
            return (byte)(state >> 24);
        }

        private static byte[] BuildFrame(byte sequence)
        {
            byte[] frame = new byte[FrameBytes];
            frame[0] = FrameMarker;
            frame[1] = sequence;

            uint state = sequence;
            byte checksum = sequence;
            for (int i = 0; i < PayloadBytes; i++)
            {
                byte value = NextByte(ref state);
                frame[2 + i] = value;
                checksum ^= value;
            }

            frame[FrameBytes - 1] = checksum;
            return frame;
        }

        private static void Transmit(PioUartTx tx)
        {
            byte sequence = 0;

            for (int i = 0; FramesToSend == 0 || i < FramesToSend; i++)
            {
                byte[] frame = BuildFrame(sequence);
                tx.Send(frame, frame.Length);
                FramesSent++;
                sequence++;
                Thread.Sleep(SendGapMs);
            }

            TxDone = 1;
        }

        private static bool ReadRun(PioUartRx rx, byte[] frame, int offset, int count)
        {
            if (UseDmaReceive)
            {
                return rx.ReceiveMany(frame, offset, count, FrameTimeoutMs) == count;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    frame[offset + i] = rx.Receive();
                }
            }
            catch (TimeoutException)
            {
                return false;
            }

            return true;
        }

        public static int FramingErrors;

        private static void BeaconFramingError()
        {
            throw new InvalidOperationException();
        }

        private static void OnPioInterrupt(PioBlock sender, PioInterruptFlags flags)
        {
            if ((flags & PioUartRx.FramingFlag) == 0)
            {
                return;
            }

            FramingErrors++;

            if (FramingErrors % BeaconEveryFrames == 0 && Beacons)
            {
                try
                {
                    BeaconFramingError();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        private static void Receive(PioUartRx rx)
        {
            byte[] frame = new byte[FrameBytes];
            int expected = -1;

            while (true)
            {
                if (rx.ReceiveMany(frame, 0, FrameBytes, FrameTimeoutMs) != FrameBytes)
                {
                    Timeouts++;
                    expected = -1;
                    Fire(false, false, false, true);
                    continue;
                }

                if (frame[0] != FrameMarker)
                {
                    int at = -1;
                    for (int i = 1; i < FrameBytes; i++)
                    {
                        if (frame[i] == FrameMarker)
                        {
                            at = i;
                            break;
                        }
                    }

                    expected = -1;
                    FramesCorrupt++;
                    Fire(false, true, false, false);

                    if (at > 0)
                    {
                        rx.ReceiveMany(frame, 0, at, FrameTimeoutMs);
                    }

                    continue;
                }

                byte sequence = frame[1];
                uint state = sequence;
                byte checksum = sequence;
                bool payloadOk = true;

                for (int i = 0; i < PayloadBytes; i++)
                {
                    byte value = frame[2 + i];
                    checksum ^= value;

                    if (value != NextByte(ref state))
                    {
                        payloadOk = false;
                    }
                }

                if (!payloadOk || frame[FrameBytes - 1] != checksum)
                {
                    FramesCorrupt++;
                    expected = -1;
                    Fire(false, true, false, false);
                    continue;
                }

                if (expected >= 0 && sequence != (byte)expected)
                {
                    FramesLost += (byte)(sequence - (byte)expected);
                    Fire(false, false, true, false);
                }

                expected = (byte)(sequence + 1);
                FramesOk++;

                if (FramesOk % BeaconEveryFrames == 0)
                {
                    Fire(true, false, false, false);
                }
            }
        }

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            float sysClockHz = Pio.SystemClock;

            Debug.WriteLine($"UART link on PIO{pio.Index}, {BaudRate} baud, sysclk {sysClockHz / 1_000_000f} MHz");
            Debug.WriteLine($"TX pin {TxPin}, RX pin {RxPin}, blocks {Pio.MinIndex}..{Pio.BlockCount - 1}, max pin {Pio.MaxPin}");

            PioUartTx tx = new PioUartTx(pio, TxPin, BaudRate, sysClockHz);
            PioUartRx rx = new PioUartRx(pio, RxPin, BaudRate, sysClockHz, FrameBytes);

            pio.Interrupt += OnPioInterrupt;

            new Thread(() => { Receive(rx); }).Start();
            new Thread(() => { Transmit(tx); }).Start();

            while (true)
            {
                Thread.Sleep(ReportEveryMs);

                int bytesOk = FramesOk * FrameBytes;
                Debug.WriteLine(
                    $"sent {FramesSent}  ok {FramesOk}  corrupt {FramesCorrupt}  lost {FramesLost}  " +
                    $"timeouts {Timeouts}  framing {FramingErrors}  good bytes {bytesOk}");
            }
        }
    }
}
