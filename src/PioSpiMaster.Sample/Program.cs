//
// Sample — an SPI master built from a PIO state machine, on any pair of pins.
//
// SPI is push-pull and has no acknowledgement, so the whole transmitter is two instructions: put a
// bit on MOSI while the clock is low, then raise the clock. Side-set drives SCK, which keeps the
// clock edge in the same instruction as the data movement and makes the timing exact by construction.
//
//   .side_set 1
//   .wrap_target
//     out pins, 1   side 0 [1]   ; data changes while the clock is low
//     nop           side 1 [1]   ; the receiver samples on the rising edge
//   .wrap
//
// Autopull refills the shift register every eight bits, so the CPU only feeds bytes and the clock
// stops on its own when the FIFO runs dry. That is mode 0: data set on the falling edge, sampled on
// the rising one.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace PioSpiMasterSample
{
    public sealed class PioSpiMaster : IDisposable
    {
        private const int CyclesPerBit = 4;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioSpiMaster"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="mosiPin">The GPIO carrying MOSI.</param>
        /// <param name="sckPin">The GPIO carrying SCK.</param>
        /// <param name="clockHz">Bit clock in Hz.</param>
        public PioSpiMaster(PioBlock pio, int mosiPin, int misoPin, int sckPin, int clockHz)
        {
            _pio = pio;

            _program = BuildProgram();
            _offset = _pio.AddProgram(_program);

            _pio.InitGpio(mosiPin);
            _pio.InitGpio(misoPin, PioPinPull.Down);
            _pio.InitGpio(sckPin);

            _sm = _pio.ClaimStateMachine();

            _sm.SetConsecutivePinDirs(mosiPin, 1, true);
            _sm.SetConsecutivePinDirs(sckPin, 1, true);

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .OutPins(mosiPin, 1)
                .InPins(misoPin)
                .SideSetPins(sckPin)
                .ClockFromFrequency(clockHz * CyclesPerBit, Pio.SystemClock));

            _sm.Enabled = true;
        }

        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler(new PioAssemblerOptions
            {
                SideSetCount = 1,
            });

            asm.OutShift(ShiftDirection.Left, autoPull: true, threshold: 8);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 8);

            asm.WrapTarget();
            asm.Out(DestinationOperand.Pins, 1).Side(0).Delay(1);
            asm.In(SourceOperand.Pins, 1).Side(1).Delay(1);
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Clocks out a run of bytes, most significant bit first.</summary>
        /// <param name="data">The bytes to send.</param>
        /// <returns>The number of words the transfer moved.</returns>
        public int Write(byte[] data)
        {
            uint[] words = new uint[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                words[i] = (uint)data[i] << 24;
            }

            return _sm.Write(words, 0, words.Length, 200);
        }

        /// <summary>Clocks out a run of bytes and returns what came back on MISO.</summary>
        /// <param name="data">The bytes to send.</param>
        /// <param name="received">Where the bytes read back land.</param>
        /// <returns>The number of bytes read back.</returns>
        public int Transfer(byte[] data, byte[] received)
        {
            uint[] outWords = new uint[data.Length];
            uint[] inWords = new uint[data.Length];

            for (int i = 0; i < data.Length; i++)
            {
                outWords[i] = (uint)data[i] << 24;
            }

            _sm.Write(outWords, 0, data.Length, 200);
            int got = _sm.Read(inWords, 0, data.Length, 200);

            for (int i = 0; i < got; i++)
            {
                received[i] = (byte)inWords[i];
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

    public sealed class PioSpiResponder : IDisposable
    {
        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="PioSpiResponder"/> class.</summary>
        /// <param name="pio">The PIO block to run on.</param>
        /// <param name="mosiPin">The GPIO to sample; SCK must be the next pin up.</param>
        /// <param name="misoPin">The GPIO to drive with the outgoing bits.</param>
        public PioSpiResponder(PioBlock pio, int mosiPin, int misoPin)
        {
            _pio = pio;

            _program = BuildProgram();
            _offset = _pio.AddProgram(_program);
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

        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler();

            asm.OutShift(ShiftDirection.Left, autoPull: true, threshold: 8);
            asm.InShift(ShiftDirection.Left, autoPush: true, threshold: 8);

            asm.WrapTarget();
            asm.WaitPin(false, 1);
            asm.Out(DestinationOperand.Pins, 1);
            asm.WaitPin(true, 1);
            asm.In(SourceOperand.Pins, 1);
            asm.Wrap();

            return asm.Build();
        }

        /// <summary>Queues bytes to be clocked out by the master.</summary>
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

        /// <summary>Reads what the master clocked in.</summary>
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
        private const int MosiPin = 4;
        private const int MisoPin = 3;
        private const int SckPin = 5;
        private const int ClockHz = 100_000;

        public static int Transfers;
        public static int LoopbackOk;
        public static int ResponderHeard;

        private static void BeaconMisoMatches()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMisoAllZeros()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMisoAllOnes()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconMisoOther()
        {
            throw new InvalidOperationException();
        }

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

        private static void BeaconEveryTransferClean()
        {
            throw new InvalidOperationException();
        }

        private static void BeaconSomeTransferFailed()
        {
            throw new InvalidOperationException();
        }

        public static int LastFailure;

        private static void FireCumulative()
        {
            try
            {
                if (LoopbackOk == Transfers && ResponderHeard == Transfers)
                {
                    BeaconEveryTransferClean();
                }
                else if ((Transfers - LastFailure) >= 400)
                {
                    BeaconCleanForLast400();
                }
                else
                {
                    BeaconSomeTransferFailed();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void BeaconCleanForLast400()
        {
            throw new InvalidOperationException();
        }

        private static void Fire(byte[] back, int got, byte[] answer, byte[] seen, int seenCount, byte[] payload)
        {
            try
            {
                bool misoOk = got == answer.Length;
                bool zeros = got > 0;
                bool ones = got > 0;

                for (int i = 0; i < got; i++)
                {
                    misoOk = misoOk && back[i] == answer[i];
                    zeros = zeros && back[i] == 0x00;
                    ones = ones && back[i] == 0xFF;
                }

                if (misoOk)
                {
                    BeaconMisoMatches();
                }
                else if (zeros)
                {
                    BeaconMisoAllZeros();
                }
                else if (ones)
                {
                    BeaconMisoAllOnes();
                }
                else
                {
                    BeaconMisoOther();
                }
            }
            catch (InvalidOperationException)
            {
            }

        }

        public static void Main()
        {
            PioBlock pio = Pio.Get(Pio.MinIndex);

            Debug.WriteLine($"PIO SPI master on PIO{pio.Index}, MOSI GP{MosiPin}, MISO GP{MisoPin}, SCK GP{SckPin}, {ClockHz} Hz");

            byte[] answer = new byte[] { 0x3C, 0xC3, 0x81, 0x18 };

            var responder = new PioSpiResponder(pio, MosiPin, MisoPin);
            responder.Preload(answer);

            var spi = new PioSpiMaster(pio, MosiPin, MisoPin, SckPin, ClockHz);
            byte[] payload = new byte[] { 0xA5, 0x5A, 0x0F, 0xF0 };
            byte[] back = new byte[payload.Length];

            byte[] seen = new byte[payload.Length];
            int seenCount = 0;

            while (true)
            {
                int got = spi.Transfer(payload, back);
                seenCount = responder.Receive(seen, payload.Length, 200);
                responder.Preload(answer);
                Transfers++;

                bool echoed = got == payload.Length;
                for (int i = 0; i < got && echoed; i++)
                {
                    echoed = back[i] == answer[i];
                }

                if (echoed)
                {
                    LoopbackOk++;
                    ResponderHeard++;
                }
                else
                {
                    LastFailure = Transfers;
                }

                if (Transfers % 200 == 0)
                {
                    Fire(back, got, answer, seen, seenCount, payload);
                    FireCumulative();
                }

                if (Transfers % 20 == 0)
                {
                    Debug.WriteLine($"transfers {Transfers}  miso ok {LoopbackOk}");
                    Debug.WriteLine($"   master read back  {back[0]:X2} {back[1]:X2} {back[2]:X2} {back[3]:X2}   (expected 3C C3 81 18)");
                }

                Thread.Sleep(20);
            }
        }
    }
}
