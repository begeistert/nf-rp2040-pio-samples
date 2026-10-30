//
// Sample — the whole PIO block at once: four state machines, two independent SPI buses.
//
// Each bus pairs the two-instruction master with the four-instruction responder from the SPI
// samples, so the block's four state machines are all claimed. The buses run at different clocks
// and each is driven from its own managed thread, so two DMA transfers are routinely in flight
// at the same time and the event plumbing gets exercised under concurrency, not just in sequence.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace PioFullBlockSample
{
    public sealed class SpiPair : IDisposable
    {
        private const int CyclesPerBit = 4;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _masterSm;
        private readonly PioStateMachine _responderSm;
        private readonly PioProgram _masterProgram;
        private readonly PioProgram _responderProgram;
        private readonly uint _masterOffset;
        private readonly uint _responderOffset;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="SpiPair"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="mosiPin">The GPIO carrying MOSI; SCK must be the next pin up.</param>
        /// <param name="misoPin">The GPIO carrying MISO.</param>
        /// <param name="clockHz">Bit clock in Hz.</param>
        public SpiPair(PioBlock pio, int mosiPin, int misoPin, int clockHz)
        {
            int sckPin = mosiPin + 1;

            _pio = pio;

            _responderProgram = BuildResponderProgram();
            _responderOffset = _pio.AddProgram(_responderProgram);
            _pio.InitGpio(misoPin);

            _responderSm = _pio.ClaimStateMachine();

            _responderSm.Init(_responderOffset, PioStateMachineConfig.FromProgram(_responderProgram, (int)_responderOffset)
                .InPins(mosiPin)
                .OutPins(misoPin, 1)
                .SetPins(misoPin, 1)
                .ClockFromFrequency(8_000_000f, Pio.SystemClock));

            _responderSm.SetConsecutivePinDirs(misoPin, 1, true);
            _responderSm.Enabled = true;

            _masterProgram = BuildMasterProgram();
            _masterOffset = _pio.AddProgram(_masterProgram);

            _pio.InitGpio(mosiPin);
            _pio.InitGpio(sckPin);

            _masterSm = _pio.ClaimStateMachine();

            _masterSm.SetConsecutivePinDirs(mosiPin, 1, true);
            _masterSm.SetConsecutivePinDirs(sckPin, 1, true);

            _masterSm.Init(_masterOffset, PioStateMachineConfig.FromProgram(_masterProgram, (int)_masterOffset)
                .OutPins(mosiPin, 1)
                .InPins(misoPin)
                .SideSetPins(sckPin)
                .ClockFromFrequency(clockHz * CyclesPerBit, Pio.SystemClock));

            _masterSm.Enabled = true;
        }

        private static PioProgram BuildMasterProgram()
        {
            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
            });

            asm.SetOutShift(ShiftDirection.Left, autoPull: true, threshold: 8);
            asm.SetInShift(ShiftDirection.Left, autoPush: true, threshold: 8);

            asm.WrapTarget();
            asm.Out(DestinationOperand.Pins, 1).Side(0).Delay(1);
            asm.In(SourceOperand.Pins, 1).Side(1).Delay(1);
            asm.Wrap();

            return asm.Build();
        }

        private static PioProgram BuildResponderProgram()
        {
            var asm = new PioAssembler();

            asm.SetOutShift(ShiftDirection.Left, autoPull: true, threshold: 8);
            asm.SetInShift(ShiftDirection.Left, autoPush: true, threshold: 8);

            asm.WrapTarget();
            asm.WaitPin(false, 1);
            asm.Out(DestinationOperand.Pins, 1);
            asm.WaitPin(true, 1);
            asm.In(SourceOperand.Pins, 1);
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Runs one round trip on the bus and verifies both directions.</summary>
        /// <param name="payload">The bytes the master sends.</param>
        /// <param name="answer">The bytes the responder echoes back.</param>
        /// <returns>True when both sides saw exactly what was sent to them.</returns>
        public bool Exchange(byte[] payload, byte[] answer)
        {
            uint[] outWords = new uint[payload.Length];
            uint[] backWords = new uint[payload.Length];
            uint[] seenWords = new uint[payload.Length];
            uint[] answerWords = new uint[answer.Length];

            for (int i = 0; i < payload.Length; i++)
            {
                outWords[i] = (uint)payload[i] << 24;
            }

            for (int i = 0; i < answer.Length; i++)
            {
                answerWords[i] = (uint)answer[i] << 24;
            }

            _responderSm.Write(answerWords, 0, answerWords.Length, 200);
            _masterSm.Write(outWords, 0, outWords.Length, 200);

            int back = _masterSm.Read(backWords, 0, payload.Length, 200);
            int seen = _responderSm.Read(seenWords, 0, payload.Length, 200);

            bool ok = back == payload.Length && seen == payload.Length;

            for (int i = 0; i < back && ok; i++)
            {
                ok = (byte)backWords[i] == answer[i];
            }

            for (int i = 0; i < seen && ok; i++)
            {
                ok = (byte)seenWords[i] == payload[i];
            }

            return ok;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _masterSm.Dispose();
            _responderSm.Dispose();
            _pio.RemoveProgram(_masterProgram, _masterOffset);
            _pio.RemoveProgram(_responderProgram, _responderOffset);
        }
    }

    public class Program
    {
        public static int Bus1Rounds;
        public static int Bus1Failures;
        public static int Bus2Rounds;
        public static int Bus2Failures;

        private static void BeaconBus1AllClean()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconBus1HadFailures()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconBus2AllClean()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconBus2HadFailures()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconFourStateMachinesLive()
        {
            throw new InvalidOperationException();
        }

        private static void Fire()
        {
            try
            {
                if (Bus1Failures == 0 && Bus1Rounds > 0)
                {
                    BeaconBus1AllClean();
                }
                else if (Bus1Failures > 0)
                {
                    BeaconBus1HadFailures();
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (Bus2Failures == 0 && Bus2Rounds > 0)
                {
                    BeaconBus2AllClean();
                }
                else if (Bus2Failures > 0)
                {
                    BeaconBus2HadFailures();
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                if (Bus1Rounds > 0 && Bus2Rounds > 0)
                {
                    BeaconFourStateMachinesLive();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void RunBus1()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            var pair = new SpiPair(pio, 4, 6, 100_000);
            byte[] payload = new byte[] { 0xA5, 0x5A, 0x0F, 0xF0 };
            byte[] answer = new byte[] { 0x3C, 0xC3, 0x81, 0x18 };

            while (true)
            {
                if (pair.Exchange(payload, answer))
                {
                    Bus1Rounds++;
                }
                else
                {
                    Bus1Rounds++;
                    Bus1Failures++;
                }

                Thread.Sleep(2);
            }
        }

        private static void RunBus2()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);
            var pair = new SpiPair(pio, 7, 9, 250_000);
            byte[] payload = new byte[] { 0x11, 0x22, 0x44, 0x88 };
            byte[] answer = new byte[] { 0xEE, 0xDD, 0xBB, 0x77 };

            while (true)
            {
                if (pair.Exchange(payload, answer))
                {
                    Bus2Rounds++;
                }
                else
                {
                    Bus2Rounds++;
                    Bus2Failures++;
                }

                Thread.Sleep(2);
            }
        }

        public static void Main()
        {
            Debug.WriteLine("Full PIO block: bus 1 on GP4/GP5/GP6 at 100 kHz, bus 2 on GP7/GP8/GP9 at 250 kHz");

            new Thread(RunBus1).Start();
            new Thread(RunBus2).Start();

            while (true)
            {
                Thread.Sleep(10_000);
                Fire();
            }
        }
    }
}
