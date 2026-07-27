//
// Sample — a logic analyser front end: sample a group of pins into memory at a fixed rate.
//
// Reading GPIOs from C# in a loop gives you a few tens of kHz and jitter you cannot bound. A state
// machine samples on its own clock, so the interval between samples is exact, and DMA carries the
// data to RAM without the CPU touching the FIFO. What you get back is a timing-accurate trace of a
// bus, a protocol, or a glitch you are hunting.
//
// The program is one instruction:
//
//   .wrap_target
//     in pins, PinCount     ; sample the pin group into the ISR
//   .wrap                   ; autopush hands a full word to the RX FIFO
//
// Autopush at a threshold of PinCount * SamplesPerWord packs several samples per 32-bit word, so
// one FIFO entry carries several time steps and the DMA moves fewer, larger items. The RX FIFO is
// also joined to 8 entries: nothing is transmitted here, so the TX half is dead weight and giving
// its space to RX doubles the cushion against a late DMA.
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace LogicCaptureSample
{
    /// <summary>Captures the state of consecutive GPIOs at a fixed sample rate.</summary>
    public sealed class PioLogicCapture : IDisposable
    {
        private const int WordBits = 32;

        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly PioProgram _program;
        private readonly uint _offset;
        private readonly int _pinCount;
        private readonly int _samplesPerWord;

        private bool _disposed;

        /// <summary>How many samples each captured word holds.</summary>
        public int SamplesPerWord => _samplesPerWord;

        public PioLogicCapture(int basePin, int pinCount, int sampleRateHz)
        {
            if (pinCount < 1 || pinCount > 16 || WordBits % pinCount != 0)
            {
                // A word has to divide evenly into samples, otherwise a sample straddles two words
                // and the trace is a pain to decode.
                throw new ArgumentException();
            }

            _pinCount = pinCount;
            _samplesPerWord = WordBits / pinCount;
            _program = BuildProgram(pinCount);

            _pio = Pio.Get(Pio.MinIndex);
            _offset = _pio.AddProgram(_program);

            for (int i = 0; i < pinCount; i++)
            {
                _pio.InitGpio(basePin + i);
            }

            _sm = _pio.ClaimStateMachine();

            // Inputs, so the pins are left as the bus drives them.
            _sm.SetConsecutivePinDirs(basePin, pinCount, false);

            _sm.Init(_offset, PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .InPins(basePin)
                .FifoJoin(PioFifoJoin.Rx)
                .ClockFromFrequency(sampleRateHz));
        }

        /// <summary>
        /// Captures <paramref name="words"/> words, each holding <see cref="SamplesPerWord"/>
        /// samples.
        /// </summary>
        /// <returns>How many words were actually captured; fewer than asked means it timed out.</returns>
        public int Capture(uint[] buffer, int words, int timeoutMs)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PioLogicCapture));
            }

            // Start from a known state: anything left from a previous capture would show up as
            // samples that never happened.
            _sm.Enabled = false;
            _sm.ClearFifos();
            _sm.Restart();
            _sm.Enabled = true;

            int captured = _sm.Read(buffer, 0, words, timeoutMs);

            _sm.Enabled = false;

            return captured;
        }

        /// <summary>Extracts one sample from a captured word.</summary>
        public uint SampleAt(uint word, int index)
        {
            if (index < 0 || index >= _samplesPerWord)
            {
                throw new ArgumentOutOfRangeException();
            }

            // Right shift means the oldest sample sits in the low bits.
            return (word >> (index * _pinCount)) & ((1u << _pinCount) - 1u);
        }

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

        private static PioProgram BuildProgram(int pinCount)
        {
            var asm = new PioAssembler();

            asm.WrapTarget();
            asm.In(SourceOperand.Pins, pinCount);
            asm.Wrap();

            asm.InShift(ShiftDirection.Right, autoPush: true, threshold: WordBits);

            return asm.Build();
        }
    }

    public class Program
    {
        private const int BasePin = 2;
        private const int PinCount = 4;
        private const int SampleRateHz = 1_000_000;
        private const int CaptureWords = 32;
        private const int CaptureTimeoutMs = 500;
        private const int PeriodMs = 2000;

        public static int LastCaptured = -1;
        public static uint LastSample;
        public static int Captures;

        public static void Main()
        {
            using var capture = new PioLogicCapture(BasePin, PinCount, SampleRateHz);

            uint[] trace = new uint[CaptureWords];

            Debug.WriteLine(
                $"Capturing GPIO {BasePin}..{BasePin + PinCount - 1} at {SampleRateHz / 1000} kHz, " +
                $"{capture.SamplesPerWord} samples per word");

            while (true)
            {
                LastCaptured = capture.Capture(trace, CaptureWords, CaptureTimeoutMs);

                if (LastCaptured > 0)
                {
                    Captures++;
                    LastSample = capture.SampleAt(trace[0], 0);
                    Debug.WriteLine($"captured {LastCaptured} words, first sample 0x{LastSample:X}");
                }
                else
                {
                    Debug.WriteLine("capture timed out with nothing sampled");
                }

                Thread.Sleep(PeriodMs);
            }
        }
    }
}
