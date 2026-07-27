//
// Diagnostic probe — blocking Put/Get park the caller on the FIFO interrupt in native code.
//
// Parking must not be the same as freezing the interpreter, and that is what this probe measures.
// Milestones are signalled by calling a distinct no-argument native, so a breakpoint on its address
// says which stage was reached without anyone having to read the managed heap:
//
//   Restart()         round trip through the loopback with the BLOCKING Put/Get pair
//   Exec()            a second round trip slow enough that Get() had to park and be woken by the IRQ
//   ClockDivRestart() one tick of the background thread (every TickIntervalMs)
//   DrainTxFifo()     the blocking Put on a stalled state machine gave up instead of hanging
//
// The decisive reading is how many ClockDivRestart hits land between the Restart hit and the
// DrainTxFifo hit. The stalled Put parks for the whole native budget (~1 s); a live scheduler lets
// the ticker through roughly TickIntervalMs apart, a scheduler frozen inside a native spin lets
// through none at all.
//
using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace PutGetProbeSample
{
    public class Program
    {
        private const uint Sentinel = 0xB10CB10C;
        private const int TickIntervalMs = 50;
        private const int FloodWords = 32;

        public static int RoundTripOk;
        public static int ParkedWakeOk;
        public static int TimedOut;
        public static int Ticks;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true);
            asm.Mov(DestinationOperand.InputShiftRegister, SourceOperand.OutputShiftRegister);
            asm.Push(ifFull: false, block: true);
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(Pio.MinIndex);
            uint offset = pio.AddProgram(program);

            PioStateMachine sm = pio.ClaimStateMachine(PioStateMachineIndex.Sm0);
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(1_000_000f));
            sm.ClearFifos();
            sm.Enabled = true;

            sm.Put(Sentinel);
            if (sm.Get() == Sentinel)
            {
                RoundTripOk = 1;
                sm.Restart();
            }

            // Slow the state machine down so the echo cannot possibly be ready when Get() looks: the
            // call has to park on RXNEMPTY and be woken by the PIO interrupt, not find the word waiting.
            sm.ClockDivisor = 65000f;
            sm.Put(Sentinel);
            if (sm.Get() == Sentinel)
            {
                ParkedWakeOk = 1;
                sm.Exec(0xA042);
            }

            PioStateMachine ticker = pio.ClaimStateMachine(PioStateMachineIndex.Sm3);
            bool ticking = true;
            var beat = new Thread(() =>
            {
                while (ticking)
                {
                    Ticks++;
                    ticker.ClockDivRestart();
                    Thread.Sleep(TickIntervalMs);
                }
            });
            beat.Start();

            sm.Enabled = false;
            sm.ClearFifos();

            try
            {
                for (int i = 0; i < FloodWords; i++)
                {
                    sm.Put(0xF10F0000 + (uint)i);
                }
            }
            catch
            {
                TimedOut = 1;
            }

            ticking = false;
            sm.DrainTxFifo();

            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
