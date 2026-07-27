//
// Sample — a UART transmitter on any GPIO, built from a PIO state machine.
//
// The RP2040 and RP2350 have two hardware UARTs. This gives you as many more as you have free
// state machines: eight on the RP2040, twelve on the RP2350, each on any pin and at any baud rate,
// with no CPU cost per byte.
//
// The whole transmitter is three instructions. Side-set drives the TX line, so the level is part
// of every instruction rather than a separate SET, and the loop stays tight enough to hold the bit
// period exactly:
//
//   .side_set 1 opt
//   .wrap_target
//     pull        side 1 [7]   ; wait for a byte, idle line high, hold one bit time
//     set x, 7    side 0 [7]   ; start bit: line low for one bit time
//   bitloop:
//     out pins, 1              ; shift the next data bit onto the line
//     jmp x--, bitloop   [6]   ; eight of them, one bit time each
//   .wrap                      ; wrap back into the pull, which drives the stop bit
//
// The state machine is clocked at 8x the baud rate, so each instruction slot plus its delay adds
// up to exactly one bit period. Bytes are handed over with Write, which moves them through a DMA
// channel paced by the TX FIFO: the CPU queues a whole buffer and walks away.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace SoftwareUartSample
{
    /// <summary>A transmit-only UART driven by a PIO state machine.</summary>
    public sealed class PioSerialPort : IDisposable
    {
        private const int CyclesPerBit = 8;
        private const int WriteTimeoutMs = 1000;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;

        private bool _disposed;

        public PioSerialPort(int txPin, int baudRate)
        {
            if (txPin < 0 || baudRate <= 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            _program = BuildProgram();

            _pio = Pio.Get(Pio.MinIndex);
            _offset = _pio.AddProgram(_program);
            _pio.InitGpio(txPin);

            _sm = _pio.ClaimStateMachine();

            // The line must be an output and idle high before the state machine starts, or the
            // receiver sees the transition into idle as a spurious start bit.
            _sm.SetConsecutivePinDirs(txPin, 1, true);

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .OutPins(txPin, 1)
                .SideSetPins(txPin)
                .ClockFromFrequency(baudRate * CyclesPerBit));

            _sm.Enabled = true;
        }

        /// <summary>Sends a string as 8N1 ASCII. Characters above 0x7F are sent as '?'.</summary>
        public int Write(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException();
            }

            byte[] ascii = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                ascii[i] = c < 0x80 ? (byte)c : (byte)'?';
            }

            return Write(ascii);
        }

        /// <summary>Sends raw bytes as 8N1.</summary>
        /// <returns>How many bytes were handed to the state machine.</returns>
        public int Write(byte[] data)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PioSerialPort));
            }

            if (data == null)
            {
                throw new ArgumentNullException();
            }

            // The FIFO is 32 bits wide and the program shifts out the low 8, so each byte takes a
            // word. One DMA transfer covers the whole buffer.
            uint[] words = new uint[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                words[i] = data[i];
            }

            return _sm.Write(words, 0, words.Length, WriteTimeoutMs);
        }

        /// <summary>Blocks until everything queued has left the FIFO.</summary>
        public void Flush()
        {
            while (_sm.TxLevel > 0)
            {
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Drop whatever is still queued rather than blocking on a line nobody may be reading.
            _sm.DrainTxFifo();
            _sm.Dispose();
            _pio.RemoveProgram(_program, _offset);
        }

        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
                SideSetOption = true,
            });

            PioLabel bitloop = asm.DefineLabel();

            asm.WrapTarget();
            asm.Pull().Side(1).Delay(7);
            asm.Set(DestinationOperand.RegisterX, 7).Side(0).Delay(7);
            asm.MarkLabel(bitloop);
            asm.Out(DestinationOperand.Pins, 1);
            asm.Jmp(PioCondition.XPostDec, bitloop).Delay(6);
            asm.Wrap();

            asm.OutShift(ShiftDirection.Right, autoPull: false, threshold: 32);

            return asm.Build();
        }
    }

    public class Program
    {
        private const int TxPin = 4;
        private const int BaudRate = 115200;
        private const int PeriodMs = 1000;

        public static int BytesSent;

        public static void Main()
        {
            using var uart = new PioSerialPort(TxPin, BaudRate);

            Debug.WriteLine($"PIO UART on GPIO {TxPin} at {BaudRate} baud");

            int line = 0;
            while (true)
            {
                BytesSent += uart.Write($"hello from PIO, line {++line}\r\n");
                uart.Flush();
                Thread.Sleep(PeriodMs);
            }
        }
    }
}
