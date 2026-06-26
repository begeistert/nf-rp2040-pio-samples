//
// Private sample — driving TWO independent WS2812 / NeoPixel strips at once on a Raspberry Pi
// Pico 2 (RP2350) or Pico 1 (RP2040) using the nanoFramework.Hardware.Pico PIO inline assembler.
//
// One PIO block has four state machines, so a single assembled WS2812 program can drive several
// strips concurrently — each on its own state machine and data pin, sharing the block's
// instruction memory. The CPU just pushes 24-bit GRB colours into each FIFO; the state machines
// bit-bang the 800 kHz protocol in hardware, independently and in parallel.
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace Ws2812Sample
{
    public class Program
    {
        // Two NeoPixel strips, one per state machine. Change to match your wiring.
        private const int Strip0Pin = 2;
        private const int Strip1Pin = 3;

        public static void Main()
        {
            PioProgram program = BuildWs2812();

            PioBlock pio = Pio.Get(0);

            // Load the program ONCE; both state machines run it from the same instruction memory.
            uint offset = pio.AddProgram(program);

            PioStateMachine strip0 = pio.ClaimStateMachine();
            strip0.Init(offset, Ws2812Config(program, offset, Strip0Pin));
            pio.InitGpio(Strip0Pin);
            strip0.SetConsecutivePinDirs(Strip0Pin, 1, true);
            strip0.Enabled = true;

            PioStateMachine strip1 = pio.ClaimStateMachine();
            strip1.Init(offset, Ws2812Config(program, offset, Strip1Pin));
            pio.InitGpio(Strip1Pin);
            strip1.SetConsecutivePinDirs(Strip1Pin, 1, true);
            strip1.Enabled = true;

            // Drive the two strips with different colours, concurrently.
            uint[] a = { Grb(64, 0, 0), Grb(0, 64, 0), Grb(0, 0, 64) };
            uint[] b = { Grb(0, 0, 64), Grb(64, 0, 0), Grb(0, 64, 0) };
            int i = 0;
            while (true)
            {
                strip0.Put(a[i] << 8);
                strip1.Put(b[i] << 8);
                i = (i + 1) % a.Length;
                Thread.Sleep(500);
            }
        }

        // WS2812 is 10 PIO cycles/bit at 800 kHz: div = sysclk(125 MHz) / (800 kHz * 10) = 15.625.
        private static PioStateMachineConfig Ws2812Config(PioProgram program, uint offset, int pin)
            => PioStateMachineConfig.FromProgram(program, (int)offset)
                .SideSetPins(pin)
                .OutPins(pin, 1)
                .ClockDivisor(15.625f);

        private static uint Grb(byte r, byte g, byte b) => (uint)((g << 16) | (r << 8) | b);

        // .side_set 1 ; .wrap_target
        //   out x, 1        side 0 [3]
        //   jmp !x do_zero  side 1 [2]
        //   jmp bitloop     side 1 [2]
        //   do_zero: nop    side 0 [2]
        // .wrap
        private static PioProgram BuildWs2812()
        {
            var asm = new PioAssembler(new PioAssemblerOptions { SideSetCount = 1 });
            PioLabel bitloop = asm.DefineLabel();
            PioLabel doZero = asm.DefineLabel();

            asm.WrapTarget();
            asm.MarkLabel(bitloop);
            asm.Out(PioDest.X, 1).Side(0).Delay(3);
            asm.Jmp(PioCondition.XZero, doZero).Side(1).Delay(2);
            asm.Jmp(PioCondition.Always, bitloop).Side(1).Delay(2);
            asm.MarkLabel(doZero);
            asm.Nop().Side(0).Delay(2);
            asm.Wrap();

            asm.OutShift(PioShiftDir.Left, autoPull: true, threshold: 24);
            return asm.Build();
        }
    }
}
