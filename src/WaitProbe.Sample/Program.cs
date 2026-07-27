//
// Diagnostic probe — minimal reproduction of the CLR scheduler stall.
//
// This is not a PIO showcase. It is the smallest program that reaches the stall: ONE state machine,
// ONE managed thread, and Read() calls that are guaranteed to expire because the state machine is
// never enabled, so nothing can fill the RX FIFO.
//
// What the sweep established, each point measured on this same probe:
//
//   timeout   rounds survived
//   ------------------------------------------------------------------
//     50 ms   30 of 30      no stall
//    500 ms   all           no stall
//   1000 ms   all           no stall
//   1300 ms   all           no stall
//   1400 ms   0             STALLS on the first Read
//   1500 ms   0             STALLS on the first Read
//   2000 ms   0             STALLS on the first Read
//
// So the stall depends on the TIMEOUT VALUE, not on how many waits happen, nor on a second managed
// thread, nor on a second state machine — 30 consecutive 50 ms reads are harmless, while a single
// 1400 ms read stops the scheduler for good. The threshold sits between 1300 and 1400 ms.
//
// Progress is signalled by calling PioBlock.ClearIrq in a loop: a cheap native at a known address,
// so a breakpoint on it shows whether the CLR keeps scheduling without reading the managed heap.
// Heartbeats are emitted before and after every read, so a debugger can tell "never got there" from
// "got there and then stopped".
//
// To sweep, change ReadTimeoutMs, rebuild and watch the breakpoint on PioBlock::ClearIrq:
//   dotnet build src/WaitProbe.Sample/WaitProbe.Sample.nfproj -c Release -t:Rebuild
//
using System.Threading;
using System.Diagnostics;
using nanoFramework.Hardware.Pico.Pio;

namespace WaitProbeSample
{
    public class Program
    {
        private const int ReadWords = 4;
        private const int ReadTimeoutMs = 1400;
        private const int ReadRounds = 3;
        private const int HeartbeatPeriodMs = 200;
        private const int HeartbeatsBefore = 3;
        private const int UnusedIrqFlag = 7;

        // Recorded for a debugger that can read the managed heap; the ClearIrq breakpoint is the
        // primary signal and needs neither of these.
        public static int ReadResult = -1;
        public static int BeatsBefore;
        public static int BeatsAfter;

        public static void Main()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true);
            asm.Push(ifFull: false, block: true);
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(Pio.MinIndex);
            uint offset = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(1_000_000f));
            sm.ClearFifos();

            for (int i = 0; i < HeartbeatsBefore; i++)
            {
                Beat(pio);
                BeatsBefore++;
                Debug.WriteLine($"before read: beat {BeatsBefore}");
            }

            // The state machine is deliberately left disabled: nothing pushes into RX, so this must
            // expire and report zero words rather than complete.
            // One trip through WaitEvents turned out to be harmless, so the probe now repeats it: a
            // heartbeat after every read makes the round at which scheduling stops directly
            // countable from a breakpoint, without reading the managed heap.
            uint[] buffer = new uint[ReadWords];
            for (int round = 1; round <= ReadRounds; round++)
            {
                Debug.WriteLine($"read {round}: entering");
                ReadResult = sm.Read(buffer, 0, ReadWords, ReadTimeoutMs);
                Debug.WriteLine($"read {round}: returned {ReadResult}");

                Beat(pio);
                BeatsAfter++;
            }

            Debug.WriteLine($"survived {ReadRounds} reads");

            while (true)
            {
                Beat(pio);
                BeatsAfter++;
            }
        }

        // Clearing an IRQ flag no program ever raises is a no-op on the hardware, which makes it a
        // clean marker: reaching the native is the whole point, not its effect.
        private static void Beat(PioBlock pio)
        {
            pio.ClearIrq(UnusedIrqFlag);
            Thread.Sleep(HeartbeatPeriodMs);
        }
    }
}
