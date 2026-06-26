//
// Private sample — blinking the on-board LED of a Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350)
// from C# via the nanoFramework.Hardware.Pico PIO inline assembler.
//
// A tiny PIO program pulls a word and drives its low bit onto the OUT pin. The CPU just pushes 1/0
// into the TX FIFO; the state machine drives the pin in hardware. The simplest "hello world" of PIO.
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace BlinkSample
{
    public class Program
    {
        // On-board LED on the Pico 1/2 (RP2040/RP2350, non-W). Change to match your wiring.
        private const int LedPin = 25;

        public static void Main()
        {
            // pull a word -> out its LSB to the pin -> loop. Shift RIGHT so OUT takes bit 0 of Put(1/0).
            var asm = new PioAssembler();
            PioLabel loop = asm.DefineLabel();
            asm.MarkLabel(loop);
            asm.Pull(ifEmpty: false, block: true);
            asm.Out(PioDest.Pins, 1);
            asm.Jmp(PioCondition.Always, loop);
            asm.OutShift(PioShiftDir.Right, autoPull: false, threshold: 32);
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset).OutPins(LedPin, 1));
            pio.InitGpio(LedPin);
            sm.SetConsecutivePinDirs(LedPin, 1, true);
            sm.Enabled = true;

            bool on = false;
            while (true)
            {
                on = !on;
                sm.Put(on ? 1u : 0u);
                Thread.Sleep(500);
            }
        }
    }
}
