//
// Sample — reading a DHT22 (AM2302) temperature/humidity sensor with a PIO state machine on a
// Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350) from C# via the nanoFramework.Hardware.Pico
// PIO inline assembler.
//
// The DHT one-wire protocol is pure timing: after an ~1 ms start pulse the sensor answers with 40
// bits, each a ~50 us low followed by a high whose LENGTH is the bit value (~26 us = 0, ~70 us = 1).
// The CPU can't bit-bang that reliably, but a PIO state machine can: it drives the start pulse,
// releases the line, then for every bit waits for the rising edge, delays past the 0/1 threshold,
// and samples the pin — pushing one byte at a time into the RX FIFO.
//
// The CPU side is a single blocking Read: the DMA channel is paced by the state machine's RX
// request, so the 5 bytes land without the CPU touching the FIFO, and the call yields the CLR while
// the transfer runs. That fits a request/response sensor better than an interrupt would — there is
// nothing to do between asking for a frame and having it — and the return value doubles as the
// error signal, since a sensor that never answers simply transfers fewer words than asked.
//
// Wiring: data pin to DataPin below with an external ~4.7k pull-up (the AM2302 breakout has one on
// board).
//
using System;
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace DhtSample
{
    /// <summary>A DHT11/DHT22 sensor driven by a PIO state machine.</summary>
    public sealed class PioDht : IDisposable
    {
        private const int FrameWords = 5;
        private const int ReadTimeoutMs = 50;

        // One PIO clock per microsecond, which is what the delay counts in the program assume.
        private const float PioClockHz = 1_000_000f;

        private readonly bool _isDht22;
        private readonly PioBlock _pio;
        private readonly PioStateMachine _sm;
        private readonly uint _offset;
        private readonly PioProgram _program;
        private readonly PioStateMachineConfig _config;

        private readonly uint[] _frame = new uint[FrameWords];
        private readonly byte[] _data = new byte[FrameWords];

        private bool _disposed;

        public PioDht(int pin, bool isDht22 = true)
        {
            _isDht22 = isDht22;
            _program = BuildProgram();

            _pio = Pio.Get(Pio.MinIndex);
            _offset = _pio.AddProgram(_program);
            _pio.InitGpio(pin);

            _sm = _pio.ClaimStateMachine();
            _config = PioStateMachineConfig.FromProgram(_program, (int)_offset)
                .SetPins(pin, 1)
                .InPins(pin)
                .ClockFromFrequency(PioClockHz);
        }

        /// <summary>Triggers a conversion and reads one frame.</summary>
        /// <exception cref="TimeoutException">The sensor did not return a full frame in time.</exception>
        /// <exception cref="InvalidOperationException">The frame arrived corrupt.</exception>
        public void Read(out float temperature, out float humidity)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PioDht));
            }

            // Restart from a known state. The FIFOs are cleared explicitly because a previous frame
            // that timed out mid-transfer leaves partial words behind: the native Read aborts the DMA
            // but deliberately does not flush the FIFO, since a caller may still want what arrived.
            _sm.Enabled = false;
            _sm.ClearFifos();
            _sm.Init(_offset, _config);
            _sm.Enabled = true;

            int received = _sm.Read(_frame, 0, FrameWords, ReadTimeoutMs);
            _sm.Enabled = false;

            if (received != FrameWords)
            {
                throw new TimeoutException($"sensor returned {received} of {FrameWords} bytes");
            }

            for (int i = 0; i < FrameWords; i++)
            {
                _data[i] = (byte)(_frame[i] & 0xFF);
            }

            if ((byte)(_data[0] + _data[1] + _data[2] + _data[3]) != _data[4])
            {
                throw new InvalidOperationException("frame checksum mismatch");
            }

            if (_isDht22)
            {
                humidity = (((_data[0] << 8) | _data[1])) / 10f;

                int rawTemperature = ((_data[2] & 0x7F) << 8) | _data[3];
                if ((_data[2] & 0x80) != 0)
                {
                    rawTemperature = -rawTemperature;
                }

                temperature = rawTemperature / 10f;
            }
            else
            {
                humidity = _data[0];
                temperature = _data[2];
            }
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

        //   set  pindirs, 1          ; drive the line
        //   set  pins, 0             ; start pulse, held ~1 ms by the loop below
        //   set  x, 31
        // start:
        //   jmp  x--, start   [31]
        //   set  pindirs, 0          ; release, the pull-up takes over
        //   wait 1 pin 0             ; line idles high
        //   wait 0 pin 0             ; sensor pulls low  ) response
        //   wait 1 pin 0             ; sensor releases   )
        // .wrap_target
        //   wait 0 pin 0             ; bit starts with a ~50 us low
        //   wait 1 pin 0             ; then a high whose length is the bit value
        //   set  x, 14
        // sample:
        //   jmp  x--, sample  [2]    ; wait past the 0/1 threshold
        //   in   pins, 1             ; sample: still high => 1, already low => 0
        // .wrap
        private static PioProgram BuildProgram()
        {
            var asm = new PioAssembler();
            PioLabel start = asm.DefineLabel();
            PioLabel sample = asm.DefineLabel();

            asm.Set(DestinationOperand.PinDirs, 1);
            asm.Set(DestinationOperand.Pins, 0);
            asm.Set(DestinationOperand.RegisterX, 31);
            asm.MarkLabel(start);
            asm.Jmp(PioCondition.XPostDec, start).Delay(31);

            asm.Set(DestinationOperand.PinDirs, 0);
            asm.WaitPin(true, 0);

            asm.WaitPin(false, 0);
            asm.WaitPin(true, 0);

            asm.WrapTarget();
            asm.WaitPin(false, 0);
            asm.WaitPin(true, 0);

            asm.Set(DestinationOperand.RegisterX, 14);
            asm.MarkLabel(sample);
            asm.Jmp(PioCondition.XPostDec, sample).Delay(2);
            asm.In(SourceOperand.Pins, 1);
            asm.Wrap();

            asm.SetInShift(ShiftDirection.Left, autoPush: true, threshold: 8);

            return asm.Build();
        }
    }

    public class Program
    {
        private const int DataPin = 2;
        private const int SamplePeriodMs = 2000;

        // Recorded for the integration test: a sensor that never answers must keep returning short
        // reads forever. If the DMA channel leaked, the transfer would start failing outright once
        // the pool ran dry instead of timing out the same way every time.
        public static int Timeouts;
        public static int Frames;

        public static void Main()
        {
            using var sensor = new PioDht(DataPin);

            Debug.WriteLine("Starting PIO DHT ambient sampling sequence...");

            while (true)
            {
                try
                {
                    sensor.Read(out float temperature, out float humidity);
                    Frames++;
                    Debug.WriteLine($"Temperature: {temperature:F1}C | Humidity: {humidity:F1}%");
                }
                catch (TimeoutException err)
                {
                    Timeouts++;
                    Debug.WriteLine($"Reading failed: {err.Message}");
                }
                catch (InvalidOperationException err)
                {
                    Debug.WriteLine($"Reading failed: {err.Message}");
                }

                Thread.Sleep(SamplePeriodMs);
            }
        }
    }
}
