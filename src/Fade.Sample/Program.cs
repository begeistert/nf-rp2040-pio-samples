//
// Private sample — fading the on-board LED of a Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350)
// from C# via the nanoFramework.Hardware.Rpi PIO inline assembler.
//
// A PIO PWM program holds the side-set pin high for (duty+1)/32 of each period; the CPU just streams
// the duty level into the TX FIFO. The state machine generates the PWM in hardware (no flicker), while
// the app ramps the duty up and down to fade the LED smoothly — 32 brightness levels.
//

using System.Threading;
using nanoFramework.Hardware.Rpi.Pio;

namespace FadeSample
{
    public class Program
    {
        // On-board LED on the Pico 1/2 (RP2040/RP2350, non-W). Change to match your wiring.
        private const int LedPin = 25;

        public static void Main()
        {
            //   pull noblock side 0     ; new duty (or reuse last), pin low at period start
            //   mov  x, osr             ; x = duty
            //   set  y, 31              ; y = period counter (32 levels)
            // loop: jmp x!=y noset
            //       jmp skip   side 1   ; counter reached duty -> pin high for the rest of the period
            // noset: nop
            // skip:  jmp y-- loop
            var asm = new PioAssembler(new PioAssemblerOptions { SideSetCount = 1, SideSetOpt = true });
            PioLabel loop = asm.DefineLabel(), noset = asm.DefineLabel(), skip = asm.DefineLabel();
            asm.Pull(ifEmpty: false, block: false).Side(0);
            asm.Mov(PioDest.X, PioSrc.Osr);
            asm.Set(PioDest.Y, 31);
            asm.MarkLabel(loop);
            asm.Jmp(PioCondition.XNotEqualY, noset);
            asm.Jmp(PioCondition.Always, skip).Side(1);
            asm.MarkLabel(noset);
            asm.Nop();
            asm.MarkLabel(skip);
            asm.Jmp(PioCondition.YPostDec, loop);
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .SideSetPins(LedPin)
                .ClockDivisor(10f));
            pio.InitGpio(LedPin);
            sm.SetConsecutivePinDirs(LedPin, 1, true);
            sm.Enabled = true;

            // Fade up and down forever (32 levels, ~30 ms per step => ~1 s each way).
            while (true)
            {
                for (uint d = 0; d <= 31; d++) { sm.Put(d); Thread.Sleep(30); }
                for (int d = 31; d >= 0; d--) { sm.Put((uint)d); Thread.Sleep(30); }
            }
        }
    }
}
