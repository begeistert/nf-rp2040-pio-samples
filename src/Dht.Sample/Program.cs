//
// Sample — reading a DHT22 (AM2302) temperature/humidity sensor with a PIO state machine on a
// Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350) from C# via the nanoFramework.Hardware.Pico
// PIO inline assembler.
//
// The DHT one-wire protocol is pure timing: after an ~1 ms start pulse the sensor answers with 40
// bits, each a ~50 us low followed by a high whose LENGTH is the bit value (~26 us = 0, ~70 us = 1).
// The CPU can't bit-bang that reliably, but a PIO state machine can: it drives the start pulse,
// releases the line, then for every bit waits for the rising edge, delays past the 0/1 threshold,
// and samples the pin — pushing one byte at a time into the RX FIFO. The CPU just reads 5 bytes and
// decodes humidity + temperature + checksum.
//
// Wiring: data pin to GPIO 15 with an external ~4.7k pull-up (the AM2302 breakout has one on board).
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace DhtSample
{
    public class Program
    {
        // DHT22 data line. Change to match your wiring.
        private const int DataPin = 15;

        // Last decoded reading, exposed in tenths (so 234 == 23.4 C, 456 == 45.6 %RH) — no floats on
        // the device. Updated each successful read.
        public static int TemperatureTenths;
        public static int HumidityTenths;
        public static int Reads;
        public static bool LastChecksumOk;

        public static void Main()
        {
            PioProgram program = BuildDht();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);
            pio.InitGpio(DataPin);
            PioStateMachine sm = pio.ClaimStateMachine();

            var data = new byte[5];
            while (true)
            {
                // Each read is one full protocol run: re-init drops the SM's PC back to the start
                // pulse, so a fresh trigger goes out and 40 new bits come back.
                sm.Enabled = false;
                sm.Init(offset, DhtConfig(program, offset));
                sm.Enabled = true;

                if (ReadFrame(sm, data))
                {
                    int humidity = (data[0] << 8) | data[1];
                    int temp = ((data[2] & 0x7F) << 8) | data[3];
                    if ((data[2] & 0x80) != 0)
                    {
                        temp = -temp;
                    }

                    LastChecksumOk = (byte)(data[0] + data[1] + data[2] + data[3]) == data[4];
                    if (LastChecksumOk)
                    {
                        HumidityTenths = humidity;
                        TemperatureTenths = temp;
                        Reads++;
                    }
                }

                // AM2302 sampling rate is 0.5 Hz — don't poll faster than every 2 s.
                Thread.Sleep(2000);
            }
        }

        // Reads the five bytes the SM pushes (humidity hi/lo, temp hi/lo, checksum). Each TryGet is
        // bounded so a missing/!=present sensor can never hang the app.
        private static bool ReadFrame(PioStateMachine sm, byte[] data)
        {
            for (int i = 0; i < 5; i++)
            {
                int guard = 0;
                uint word;
                while (!sm.TryGet(out word))
                {
                    if (++guard > 200000)
                    {
                        return false; // no sensor / no response
                    }
                }

                data[i] = (byte)(word & 0xFF);
            }

            return true;
        }

        // 1 us per cycle: every delay in the program is then a count of microseconds.
        private static PioStateMachineConfig DhtConfig(PioProgram program, uint offset)
            => PioStateMachineConfig.FromProgram(program, (int)offset)
                .SetPins(DataPin, 1)
                .InPins(DataPin)
                .ClockFromFrequency(1_000_000f);

        // .program dht
        //   set  pindirs, 1          ; drive the line
        //   set  pins, 0             ; start pulse: low ...
        //   set  x, 31
        // start: jmp x-- start [31]  ; ... for ~1 ms
        //   set  pindirs, 0          ; release to input
        //   wait 1 pin 0             ; line floats up (pull-up)
        //   wait 0 pin 0             ; sensor's 80 us low response
        //   wait 1 pin 0             ; sensor's 80 us high response
        // .wrap_target
        //   wait 0 pin 0             ; bit's ~50 us low
        //   wait 1 pin 0             ; bit's data pulse goes high
        //   set  x, 14
        // samp: jmp x-- samp [2]     ; wait ~45 us past the rising edge
        //   in   pins, 1             ; high here => 1 (70 us), low => 0 (26 us); autopush every 8
        // .wrap
        private static PioProgram BuildDht()
        {
            var asm = new PioAssembler();
            PioLabel start = asm.DefineLabel();
            PioLabel samp = asm.DefineLabel();

            asm.Set(PioDest.PinDirs, 1);
            asm.Set(PioDest.Pins, 0);
            asm.Set(PioDest.X, 31);
            asm.MarkLabel(start);
            asm.Jmp(PioCondition.XPostDec, start).Delay(31);
            asm.Set(PioDest.PinDirs, 0);
            asm.WaitPin(true, 0);   // line floats up on the pull-up after release
            asm.WaitPin(false, 0);  // sensor pulls low (response)
            asm.WaitPin(true, 0);   // sensor's high response

            asm.WrapTarget();
            asm.WaitPin(false, 0);
            asm.WaitPin(true, 0);
            asm.Set(PioDest.X, 14);
            asm.MarkLabel(samp);
            asm.Jmp(PioCondition.XPostDec, samp).Delay(2);
            asm.In(PioSrc.Pins, 1);
            asm.Wrap();

            asm.InShift(PioShiftDir.Left, autoPush: true, threshold: 8);
            return asm.Build();
        }
    }
}
