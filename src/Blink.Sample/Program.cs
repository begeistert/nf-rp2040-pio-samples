//
// Private sample — blinking the on-board LED of a Raspberry Pi Pico 1 (RP2040) / Pico 2 (RP2350)
// from C# via the nanoFramework.Hardware.Pico PIO inline assembler.
//
// A tiny PIO program pulls a word and drives its low bit onto the OUT pin. The CPU just pushes 1/0
// into the TX FIFO; the state machine drives the pin in hardware. The simplest "hello world" of PIO.
//
// It also shows the two ways the CPU hears back from a state machine: a managed Timer driving the
// FIFO from the CLR side, and a PIO IRQ raised by the program itself and delivered as an event. The
// interrupt needs no acknowledgement from managed code — the native handler clears the flag before
// it posts the event.
//

using System;
using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace BlinkSample
{
    public class Program
    {
        private const int LedPin = 25; // on-board LED on the Pico 1/2 non-W
        private const int BlinkPeriodMs = 500;

        private static PioStateMachine _sm;
        private static bool _ledState;

        public static void Main()
        {
            Console.WriteLine("Starting structured PIO blink...");

            PioProgram program = BuildPioProgram();

            PioBlock pio = Pio.Get(0);
            uint offset = pio.AddProgram(program);

            _sm = pio.ClaimStateMachine();

            pio.InitGpio(LedPin);

            // Pin directions have to be set while the state machine is stopped: the call borrows
            // PINCTRL to exec the SET instructions and a running program would see the borrowed value.
            _sm.SetConsecutivePinDirs(LedPin, 1, true);

            _sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .OutPins(LedPin, 1));
            _sm.Enabled = true;

            pio.Interrupt += OnPioInterrupt;

            var blinkTimer = new Timer(BlinkTimerCallback, null, 0, BlinkPeriodMs);

            Thread.Sleep(Timeout.Infinite);
        }

        //   loop:
        //     pull block         ; wait for the CPU to hand over a level
        //     out  pins, 1       ; drive its low bit onto the LED pin
        //     irq  0             ; tell the CPU the pin changed
        //     jmp  loop
        private static PioProgram BuildPioProgram()
        {
            var asm = new PioAssembler();
            PioLabel loop = asm.DefineLabel();

            asm.MarkLabel(loop);
            asm.Pull(ifEmpty: false, block: true);
            asm.Out(DestinationOperand.Pins, 1);
            asm.Irq(0);
            asm.Jmp(PioCondition.Always, loop);

            asm.OutShift(ShiftDirection.Right, autoPull: false, threshold: 32);

            return asm.Build();
        }

        private static void BlinkTimerCallback(object state)
        {
            _ledState = !_ledState;

            // TryPut rather than Put: a timer callback must not block, and a full FIFO here only means
            // the state machine has not caught up yet, which the next tick fixes on its own.
            _sm.TryPut(_ledState ? 1u : 0u);
        }

        private static void OnPioInterrupt(PioBlock sender, PioInterruptFlags flags)
        {
            if ((flags & PioInterruptFlags.Sm0) != 0)
            {
                Console.WriteLine($"[Hardware IRQ] PIO reports the LED changed to: {_ledState}");
            }
        }
    }
}
