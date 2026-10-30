//
// Sample — the PIO as an asynchronous co-processor.
//
// A device on the other end of the wire decides when it talks. The bursts are short, the intervals
// between them are unpredictable, and the bit timing is far too tight for managed code. That is the
// shape of most real sensor protocols, and it is the case this sample exists to show.
//
// The state machine does the bit-level decoding on its own. When it has a whole packet it executes
// an IRQ instruction, which the CLR delivers as a managed event. The handler then lifts the packet
// out of the FIFO with a DMA read and hands it to application code. No thread is ever parked waiting
// for a device that may not speak for seconds, which is the same bargain SerialPort.DataReceived
// makes for a hardware UART -- only here the protocol is whatever the state machine says it is.
//
// Both boards run this image and play both roles at once, so a single pair of wires demonstrates it
// in both directions:
//
//     A.GND  --  B.GND
//     A.GP2  ->  B.GP3
//     B.GP2  ->  A.GP3
//
// Receive program. The Y register counts the packet down; the IRQ fires on the last byte, and Y is
// reloaded for the next one:
//
//   .wrap_target
//   start:
//     wait 0 pin 0             ; falling edge of the start bit
//     set x, 7    [10]         ; eight bits, and slide to the centre of bit 0
//   bitloop:
//     in pins, 1
//     jmp x--, bitloop   [6]
//     jmp pin, good_stop       ; the stop bit must be high
//     irq 1                    ; framing error: tell the application and resynchronise
//     wait 1 pin 0
//     set y, PacketBytes-1     ; the packet is lost, so restart the count with it
//     jmp start
//   good_stop:
//     push
//     jmp y--, start           ; more bytes to come
//     irq 0                    ; packet complete
//     set y, PacketBytes-1
//   .wrap
//
// The packet has to fit in the RX FIFO, because those bytes sit there from the moment the IRQ is
// raised until the handler drains them. Joining the FIFOs gives eight words instead of four, which
// is what sets the packet size here.
//
// The handler runs on the event dispatch thread, so it must return before the next event arrives.
// It checks the FIFO level before reading and uses a short timeout: an event that arrives without a
// packet behind it is counted and dropped, never waited on. A handler that blocks on a line that has
// gone quiet stalls every other event in the queue behind it.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace PioCoprocessorSample
{
    public sealed class PioSensorLink : IDisposable
    {
        private const int CyclesPerBit = 8;
        private const int SendTimeoutMs = 500;
        private const int DrainTimeoutMs = 5;

        internal const int PacketBytes = 8;
        internal const int PacketIrq = 0;
        internal const int FramingIrq = 1;
        internal const PioInterruptFlags PacketFlag = PioInterruptFlags.Sm0;
        internal const PioInterruptFlags FramingFlag = PioInterruptFlags.Sm1;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _tx;
        private readonly PioStateMachine _rx;
        private readonly PioProgram _txProgram;
        private readonly PioProgram _rxProgram;
        private readonly uint _txOffset;
        private readonly uint _rxOffset;
        private readonly uint[] _words;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioSensorLink"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="txPin">The GPIO to drive.</param>
        /// <param name="rxPin">The GPIO to sample.</param>
        /// <param name="baudRate">Bits per second.</param>
        public PioSensorLink(PioBlock pio, int txPin, int rxPin, int baudRate)
        {
            float sysClockHz = Pio.SystemClock;
            _words = new uint[PacketBytes];
            _pio = pio;

            _txProgram = BuildTxProgram();
            _txOffset = _pio.AddProgram(_txProgram);
            _pio.InitGpio(txPin);
            _tx = _pio.ClaimStateMachine();
            _tx.SetConsecutivePinDirs(txPin, 1, true);
            _tx.Init(_txOffset, PioStateMachineConfig.FromProgram(_txProgram, (int)_txOffset)
                .OutPins(txPin, 1)
                .SideSetPins(txPin)
                .ClockFromFrequency(baudRate * CyclesPerBit, sysClockHz));
            _tx.Enabled = true;

            _rxProgram = BuildRxProgram();
            _rxOffset = _pio.AddProgram(_rxProgram);
            _pio.InitGpio(rxPin);
            _rx = _pio.ClaimStateMachine();
            _rx.SetConsecutivePinDirs(rxPin, 1, false);
            _rx.Init(_rxOffset, PioStateMachineConfig.FromProgram(_rxProgram, (int)_rxOffset)
                .InPins(rxPin)
                .JmpPin(rxPin)
                .FifoJoin(PioFifoJoin.Rx)
                .ClockFromFrequency(baudRate * CyclesPerBit, sysClockHz));
            _rx.Enabled = true;
        }

        private static PioProgram BuildTxProgram()
        {
            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
                SideSetOption = true,
            });

            asm.SetOutShift(ShiftDirection.Right, autoPull: false, threshold: 32);

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

        private static PioProgram BuildRxProgram()
        {
            var asm = new PioAssembler();

            asm.SetInShift(ShiftDirection.Right, autoPush: false, threshold: 32);

            PioLabel start = asm.DefineLabel();
            PioLabel bitloop = asm.DefineLabel();
            PioLabel goodStop = asm.DefineLabel();

            asm.Set(DestinationOperand.RegisterY, PacketBytes - 1);
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
            asm.Set(DestinationOperand.RegisterY, PacketBytes - 1);
            asm.Jmp(start);
            asm.MarkLabel(goodStop);
            asm.Push();
            asm.Jmp(PioCondition.YPostDec, start);
            asm.Irq(PacketIrq);
            asm.Set(DestinationOperand.RegisterY, PacketBytes - 1);
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Sends one packet.</summary>
        /// <param name="packet">The bytes to send; must be <see cref="PacketBytes"/> long.</param>
        /// <returns>The number of bytes the transfer moved.</returns>
        public int Send(byte[] packet)
        {
            uint[] words = new uint[PacketBytes];
            for (int i = 0; i < PacketBytes; i++)
            {
                words[i] = packet[i];
            }

            return _tx.Write(words, 0, PacketBytes, SendTimeoutMs);
        }

        /// <summary>
        /// Lifts a complete packet out of the RX FIFO. Called from the interrupt handler, where the
        /// bytes are already waiting, so the transfer returns without waiting on the line.
        /// </summary>
        /// <param name="packet">Where the bytes land.</param>
        /// <returns>The number of bytes read.</returns>
        public bool PacketWaiting
        {
            get
            {
                return _rx.RxLevel >= PacketBytes;
            }
        }

        public int Receive(byte[] packet)
        {
            int got = _rx.Read(_words, 0, PacketBytes, DrainTimeoutMs);

            for (int i = 0; i < got; i++)
            {
                packet[i] = (byte)(_words[i] >> 24);
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
            _tx.Dispose();
            _rx.Dispose();
            _pio.RemoveProgram(_txProgram, _txOffset);
            _pio.RemoveProgram(_rxProgram, _rxOffset);
        }
    }

    public class Program
    {
        private const int BaudRate = 9600;
        private const int TxPin = 2;
        private const int RxPin = 3;
        private const int ReportEveryMs = 1000;
        private const int BeaconEvery = 20;

        private const byte PacketMarker = 0xC3;
        private const int MinGapMs = 150;
        private const int GapSpreadMs = 900;

        public static int PacketsSent;
        public static int PacketsOk;
        public static int PacketsBad;
        public static int FramingErrors;
        public static int MainLoopWork;
        public static int SpuriousEvents;

        private static PioSensorLink _link;
        private static byte[] _rxPacket;

        private static void BeaconPacketOk()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconPacketBad()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFramingError()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMainAlive()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconSpuriousEvent()
        {
            throw new InvalidOperationException();
        }

        private static void Fire(int kind)
        {
            try
            {
                if (kind == 0)
                {
                    BeaconPacketOk();
                }
                else if (kind == 1)
                {
                    BeaconPacketBad();
                }
                else if (kind == 2)
                {
                    BeaconFramingError();
                }
                else if (kind == 3)
                {
                    BeaconMainAlive();
                }
                else
                {
                    BeaconSpuriousEvent();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static byte Checksum(byte[] packet, int count)
        {
            byte sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum ^= packet[i];
            }

            return sum;
        }

        private static void OnPioInterrupt(PioBlock sender, PioInterruptFlags flags)
        {
            if ((flags & PioSensorLink.FramingFlag) != 0)
            {
                FramingErrors++;

                if (FramingErrors % BeaconEvery == 0)
                {
                    Fire(2);
                }
            }

            if ((flags & PioSensorLink.PacketFlag) == 0)
            {
                return;
            }

            if (!_link.PacketWaiting)
            {
                SpuriousEvents++;

                if (SpuriousEvents % BeaconEvery == 0)
                {
                    Fire(4);
                }

                return;
            }

            if (_link.Receive(_rxPacket) != PioSensorLink.PacketBytes)
            {
                PacketsBad++;
                Fire(1);
                return;
            }

            if (_rxPacket[0] != PacketMarker ||
                _rxPacket[PioSensorLink.PacketBytes - 1] != Checksum(_rxPacket, PioSensorLink.PacketBytes - 1))
            {
                PacketsBad++;
                Fire(1);
                return;
            }

            PacketsOk++;

            if (PacketsOk % BeaconEvery == 0)
            {
                Fire(0);
            }
        }

        private static void Sensor(PioSensorLink link)
        {
            byte[] packet = new byte[PioSensorLink.PacketBytes];
            uint state = 12345;
            byte reading = 0;

            while (true)
            {
                packet[0] = PacketMarker;
                for (int i = 1; i < PioSensorLink.PacketBytes - 1; i++)
                {
                    packet[i] = reading;
                    reading++;
                }

                packet[PioSensorLink.PacketBytes - 1] = Checksum(packet, PioSensorLink.PacketBytes - 1);

                link.Send(packet);
                PacketsSent++;

                state = (state * 1664525u) + 1013904223u;
                Thread.Sleep(MinGapMs + (int)((state >> 20) % GapSpreadMs));
            }
        }

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            Debug.WriteLine($"PIO co-processor on PIO{pio.Index}, {BaudRate} baud, sysclk {Pio.SystemClock / 1_000_000f} MHz");

            _link = new PioSensorLink(pio, TxPin, RxPin, BaudRate);
            _rxPacket = new byte[PioSensorLink.PacketBytes];

            pio.Interrupt += OnPioInterrupt;

            new Thread(() => { Sensor(_link); }).Start();

            while (true)
            {
                MainLoopWork++;

                if (MainLoopWork % 200 == 0)
                {
                    Fire(3);
                }

                if (MainLoopWork % (ReportEveryMs / 5) == 0)
                {
                    Debug.WriteLine(
                        $"sent {PacketsSent}  ok {PacketsOk}  bad {PacketsBad}  " +
                        $"framing {FramingErrors}  spurious {SpuriousEvents}  main-loop {MainLoopWork}");
                }

                Thread.Sleep(5);
            }
        }
    }
}
