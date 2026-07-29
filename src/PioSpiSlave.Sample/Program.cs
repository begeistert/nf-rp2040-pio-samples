//
// Sample — the receiving half of an SPI link, clocked by the other end.
//
// The master owns the clock, so this side never generates one: it waits on SCK and moves a bit on
// each edge. Data goes out on MISO while the clock is low and the incoming bit is sampled while it
// is high, which is the same mode 0 the master drives.
//
//   .wrap_target
//     wait 0 gpio SCK   ; SCK low
//     out pins, 1       ; put the outgoing bit on MISO
//     wait 1 gpio SCK   ; SCK high
//     in pins, 1        ; sample MOSI
//   .wrap
//
// The waits use absolute GPIO numbering, so SCK can live on any pin relative to MOSI. Autopull and
// autopush at eight bits turn the whole thing into a byte pipe in both directions. Internal
// pull-downs on SCK and MOSI keep the lines quiet while the master side is still booting, so no
// phantom edges consume bits before the first real transaction.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace PioSpiSlaveSample
{
    public sealed class PioSpiResponder : IDisposable
    {
        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioSpiResponder"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="mosiPin">The GPIO to sample for incoming bits.</param>
        /// <param name="sckPin">The GPIO carrying the master's clock.</param>
        /// <param name="misoPin">The GPIO to drive with the outgoing bits.</param>
        public PioSpiResponder(PioBlock pio, int mosiPin, int sckPin, int misoPin)
        {
            _pio = pio;

            _program = BuildProgram(sckPin);
            _offset = _pio.AddProgram(_program);

            _pio.InitGpio(mosiPin, PioPinPull.Down);
            _pio.InitGpio(sckPin, PioPinPull.Down);
            _pio.InitGpio(misoPin);

            _sm = _pio.ClaimStateMachine();

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .InPins(mosiPin)
                .OutPins(misoPin, 1)
                .SetPins(misoPin, 1)
                .ClockFromFrequency(8_000_000f, Pio.SystemClock));

            _sm.SetConsecutivePinDirs(misoPin, 1, true);

            _sm.Enabled = true;
        }

        private static PioProgram BuildProgram(int sckPin)
        {
            var asm = new PioAssembler();

            asm.OutShift(ShiftDirection.Left, autoPull: true, threshold: 8);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 8);

            asm.WrapTarget();
            asm.WaitGpio(false, sckPin);
            asm.Out(DestinationOperand.Pins, 1);
            asm.WaitGpio(true, sckPin);
            asm.In(SourceOperand.Pins, 1);
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Queues bytes to be clocked out on MISO by the master's clock.</summary>
        /// <param name="data">The bytes to queue.</param>
        /// <returns>The number of words queued.</returns>
        public int Preload(byte[] data)
        {
            uint[] words = new uint[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                words[i] = (uint)data[i] << 24;
            }

            return _sm.Write(words, 0, words.Length, 200);
        }

        /// <summary>Resynchronizes with the master after a garbled or missed batch. With no
        /// chip-select line to frame transactions, a slipped clock edge stays skewed forever, and
        /// re-enabling mid-burst just picks a new skew: the pause lets the burst in flight die out
        /// so the state machine comes back up inside an idle gap.</summary>
        /// <param name="answer">The bytes to queue for the next transaction.</param>
        public void Resync(byte[] answer)
        {
            _sm.Enabled = false;
            _sm.ClearFifos();
            _sm.Restart();
            _sm.Exec((ushort)_offset);
            Thread.Sleep(5);
            Preload(answer);
            _sm.Enabled = true;
        }

        /// <summary>Reads bytes the master has clocked in.</summary>
        /// <param name="destination">Where the bytes land.</param>
        /// <param name="count">How many to read.</param>
        /// <param name="timeoutMs">How long to wait.</param>
        /// <returns>The number of bytes read.</returns>
        public int Receive(byte[] destination, int count, int timeoutMs)
        {
            uint[] words = new uint[count];
            int got = _sm.Read(words, 0, count, timeoutMs);

            for (int i = 0; i < got; i++)
            {
                destination[i] = (byte)words[i];
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
        private const int MosiPin = 5;
        private const int SckPin = 4;
        private const int MisoPin = 2;
        private const int Batch = 4;

        public static int Batches;
        public static int Matches;
        public static int Silent;
        public static int Resyncs;
        public static int LastFailure;

        private static void BeaconMosiMatches()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMosiWrong()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMosiSilent()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconCleanForLast100()
        {
            throw new InvalidOperationException();
        }

        private static void Fire(bool matched, int n)
        {
            try
            {
                if (n == 0)
                {
                    BeaconMosiSilent();
                }
                else if (matched)
                {
                    BeaconMosiMatches();
                }
                else
                {
                    BeaconMosiWrong();
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (Matches >= 100 && (Batches - LastFailure) >= 100)
                {
                    BeaconCleanForLast100();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            Debug.WriteLine($"PIO SPI responder on PIO{pio.Index}, MOSI GP{MosiPin}, SCK GP{SckPin}, MISO GP{MisoPin}");

            var spi = new PioSpiResponder(pio, MosiPin, SckPin, MisoPin);
            byte[] answer = new byte[] { 0x3C, 0xC3, 0x81, 0x18 };
            byte[] got = new byte[Batch];

            spi.Preload(answer);

            while (true)
            {
                int n = spi.Receive(got, Batch, 1000);

                if (n > 0)
                {
                    Batches++;
                }

                bool matched = n == Batch && got[0] == 0xA5 && got[1] == 0x5A && got[2] == 0x0F && got[3] == 0xF0;

                if (matched)
                {
                    Matches++;
                    spi.Preload(answer);
                }
                else
                {
                    if (n > 0)
                    {
                        LastFailure = Batches;
                        Resyncs++;
                    }
                    else
                    {
                        Silent++;
                    }

                    spi.Resync(answer);
                }

                if ((Batches > 0 && Batches % 100 == 0) || (Silent > 0 && Silent % 10 == 0))
                {
                    Fire(matched, n);
                }
            }
        }
    }
}
