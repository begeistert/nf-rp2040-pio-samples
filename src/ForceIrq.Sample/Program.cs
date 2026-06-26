//
// Sample — raising a PIO interrupt from the CPU side: the app calls ForceIrq and the managed
// NativeEventDispatcher handler fires (the SM raises nothing here). Useful for CPU->SM signalling.
//

using System.Threading;
using nanoFramework.Hardware.Rpi.Pio;
using nanoFramework.Runtime.Events;

namespace ForceIrqSample
{
    public class Program
    {
        public static int IrqCount;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Nop();
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            pio.AddProgram(program);

            var irq = new NativeEventDispatcher("PioIrqDriver", 0UL);
            irq.OnInterrupt += (flags, _, time) => { IrqCount++; };
            irq.EnableInterrupt();

            while (true)
            {
                pio.ForceIrq(0);
                Thread.Sleep(500);
            }
        }
    }
}
