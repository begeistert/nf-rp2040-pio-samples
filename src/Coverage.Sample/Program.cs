//
// Sample — coverage harness for the PIO native interop. Not a "pretty" demo: it drives the FIFO
// loopback program and exercises the native methods the other showcases never reach (RemoveProgram,
// Exec, Restart, ClockDivRestart, DrainTxFifo, Unclaim via Dispose, GetRxLevel, SetClockDivisor,
// ClearIrq) plus the input-validation paths. Each method's effect is routed through the FIFO with a
// distinct sentinel so the integration test can OBSERVE it through the PioProbe (TxOf/RxOf), not just
// read back a recorded value. A few methods have no FIFO-observable effect and are honest smoke checks
// (called out below); their static fields are still recorded for the test.
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace CoverageSample
{
    public class Program
    {
        // Distinct sentinels the test looks for in the probe's FIFO capture (block 0, SM 0).
        public const uint ExecSentinel = 21;          // pushed into RX *only* by Exec (never via TX)
        public const uint RestartSentinel = 0xBEEF;   // echoes after Restart
        public const uint DrainKept = 0xDDDD0003;     // echoes after DrainTxFifo
        public const uint DrainDropped1 = 0xDDDD0001; // pushed to TX then drained -> must NOT echo
        public const uint DrainDropped2 = 0xDDDD0002;
        public const uint ClockSentinel = 0xC0DE;     // echoes after SetClockDivisor/ClockDivRestart
        public const uint ReuseSentinel = 0xABCDABCD; // echoes after RemoveProgram + re-add

        public static int RemoveReuseOffset = -1; // second AddProgram offset after RemoveProgram (expect 0)
        public static int UnclaimWorks;           // 1 if exhaust/dispose/re-claim proves Unclaim frees a slot
        public static int RxLevelSeen = -1;       // GetRxLevel reading while words sit in RX (expect > 0)
        public static int RemoveEchoOk;           // 1 if the re-added program echoed its sentinel
        public static int RemoveUnownedThrew;     // native: RemoveProgram of a range that was never allocated
        public static int InitBadOffsetThrew;     // native: Init with an offset outside 0..31
        public static int PinDirsBadThrew;        // managed backstop: SetConsecutivePinDirs basePin > 47
        public static int InitGpioBadThrew;       // managed backstop: InitGpio pin > 47
        public static int Done;                   // set last, so the test knows recording finished

        // The smallest program that uses both FIFOs: TX -> OSR -> ISR -> RX.
        private static PioProgram BuildLoopback()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true);
            asm.Mov(PioDest.Isr, PioSrc.Osr);
            asm.Push(ifFull: false, block: true);
            asm.Wrap();
            return asm.Build();
        }

        // Push a word and read its echo back, bounded so a stalled SM can't hang us. Both the TryPut and
        // the TryGet are seen by the probe (TxOf/RxOf).
        private static bool Echo(PioStateMachine sm, uint value)
        {
            if (!sm.TryPut(value))
            {
                return false;
            }

            uint got;
            int guard = 0;
            while (!sm.TryGet(out got) && guard++ < 4000)
            {
            }

            return guard < 4000;
        }

        public static void Main()
        {
            PioProgram program = BuildLoopback();

            PioBlock pio = Pio.Get(0);
            uint offset0 = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset0, PioStateMachineConfig.FromProgram(program, (int)offset0)
                .ClockFromFrequency(1_000_000f));
            sm.ClearFifos();
            sm.Enabled = true;

            // --- Exec (probe-observed) ---
            // With the SM stalled on the blocking PULL (empty TX), inject SET X,21 / IN X,32 / PUSH out of
            // band. That places 21 into RX without any TX push, so the test seeing 21 in RxOf — and never in
            // TxOf — proves Exec actually executed instructions.
            var enc = new PioAssembler();
            enc.Set(PioDest.X, (int)ExecSentinel);
            enc.In(PioSrc.X, 32);
            enc.Push(ifFull: false, block: true);
            ushort[] exec = enc.Build().Instructions;
            sm.Exec(exec[0]);
            sm.Exec(exec[1]);
            sm.Exec(exec[2]);
            uint execGot;
            int g0 = 0;
            while (!sm.TryGet(out execGot) && g0++ < 4000)
            {
            }

            // --- GetRxLevel (probe-observed cross-check) ---
            // Push three words without draining so they pile up in RX, read the native level, then drain
            // them (the probe pulls all three). RxLevelSeen > 0 means the native FLEVEL read saw the words
            // the probe then confirms crossed the FIFO.
            sm.TryPut(0x101);
            sm.TryPut(0x202);
            sm.TryPut(0x303);
            int spin = 0;
            while (sm.GetRxLevel() < 3 && spin++ < 4000)
            {
            }

            RxLevelSeen = (int)sm.GetRxLevel();
            uint drainGot;
            int g1 = 0;
            while (sm.TryGet(out drainGot) && g1++ < 16)
            {
            }

            // --- Restart (probe-observed) ---
            sm.Restart();
            Echo(sm, RestartSentinel);

            // --- DrainTxFifo (probe-observed) ---
            // Park two words in TX with the SM disabled (the probe sees the TX pushes), drain them, then
            // re-enable and echo a marker. The drained words must never appear in RX.
            sm.Enabled = false;
            sm.TryPut(DrainDropped1);
            sm.DrainTxFifo();
            sm.Enabled = true;
            Echo(sm, DrainKept);

            // --- SetClockDivisor + ClockDivRestart (smoke: not FIFO-observable) ---
            // The probe timestamps the firmware's TryPut/TryGet, not the PIO clock, so a clock change can't
            // be proven through the FIFO here. We only prove the SM keeps working after re-clocking by
            // echoing a sentinel.
            sm.SetClockDivisor(2f);
            sm.ClockDivRestart();
            Echo(sm, ClockSentinel);

            // --- ClearIrq (smoke) ---
            // No managed IRQ-flag read-back, so this only proves Force/Clear run without locking up.
            pio.ForceIrq(0);
            pio.ClearIrq(0);

            // --- RemoveProgram (probe-observed) ---
            // Free the SM and the program slots, re-add (reuses offset 0), then RUN the reloaded program and
            // echo a sentinel — proving the slots were reclaimed and the program reloaded and still works.
            sm.Enabled = false;
            sm.Dispose(); // Unclaim
            pio.RemoveProgram(program, offset0);
            uint offset1 = pio.AddProgram(program);
            RemoveReuseOffset = (int)offset1;
            PioStateMachine sm1 = pio.ClaimStateMachine();
            sm1.Init(offset1, PioStateMachineConfig.FromProgram(program, (int)offset1)
                .ClockFromFrequency(1_000_000f));
            sm1.ClearFifos();
            sm1.Enabled = true;
            RemoveEchoOk = Echo(sm1, ReuseSentinel) ? 1 : 0;

            // --- Unclaim proof (bookkeeping, not FIFO) ---
            // Fill a fresh block; the 5th claim fails, dispose one, the next succeeds.
            PioBlock pioB = Pio.Get(1);
            PioStateMachine[] claimed = new PioStateMachine[4];
            for (int i = 0; i < 4; i++)
            {
                claimed[i] = pioB.ClaimStateMachine();
            }

            bool fifthThrew = false;
            try
            {
                pioB.ClaimStateMachine();
            }
            catch
            {
                fifthThrew = true;
            }

            claimed[1].Dispose();

            bool reclaimOk = false;
            try
            {
                pioB.ClaimStateMachine();
                reclaimOk = true;
            }
            catch
            {
            }

            UnclaimWorks = (fifthThrew && reclaimOk) ? 1 : 0;

            // --- Validation that reaches the NATIVE checks (managed does not pre-check these) ---
            try
            {
                pio.RemoveProgram(program, 25); // [25,28) was never allocated -> native ownership check
            }
            catch
            {
                RemoveUnownedThrew = 1;
            }

            PioStateMachine sm2 = pio.ClaimStateMachine();
            try
            {
                sm2.Init(99, PioStateMachineConfig.FromProgram(program, 0)); // offset 99 > 31 -> native check
            }
            catch
            {
                InitBadOffsetThrew = 1;
            }

            // --- Validation caught at the MANAGED layer (native overflow guards sit behind these) ---
            try
            {
                sm2.SetConsecutivePinDirs(50, 1, true); // basePin > 47
            }
            catch
            {
                PinDirsBadThrew = 1;
            }

            try
            {
                pio.InitGpio(99); // pin > 47
            }
            catch
            {
                InitGpioBadThrew = 1;
            }

            Done = 1;

            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
