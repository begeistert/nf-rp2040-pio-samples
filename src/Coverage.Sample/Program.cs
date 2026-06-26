//
// Sample — coverage harness for the PIO native interop. It is not a "pretty" demo: it drives the
// FIFO loopback program and then calls the native methods the other showcases never reach
// (RemoveProgram, Exec, Restart, ClockDivRestart, DrainTxFifo, Unclaim via Dispose, GetRxLevel,
// SetClockDivisor, ClearIrq) plus the input-validation paths, recording observable results into
// static fields that the integration test reads back.
//

using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace CoverageSample
{
    public class Program
    {
        public static int RemoveReuseOffset = -1; // second AddProgram offset after RemoveProgram (expect 0)
        public static int UnclaimWorks;           // 1 if exhaust/dispose/re-claim proves Unclaim frees a slot
        public static int RxLevel = -1;           // GetRxLevel reading
        public static int MethodsRun;             // Exec/Restart/ClockDivRestart/DrainTxFifo/SetClockDivisor/ClearIrq
        public static int RemoveUnownedThrew;     // native: RemoveProgram of a range that was never allocated
        public static int InitBadOffsetThrew;     // native: Init with an offset outside 0..31
        public static int PinDirsBadThrew;        // managed backstop: SetConsecutivePinDirs basePin > 47
        public static int InitGpioBadThrew;       // managed backstop: InitGpio pin > 47
        public static int Done;                   // set last, so the test knows recording finished

        public static void Main()
        {
            // The smallest program that uses both FIFOs: TX -> OSR -> ISR -> RX.
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true);
            asm.Mov(PioDest.Isr, PioSrc.Osr);
            asm.Push(ifFull: false, block: true);
            asm.Wrap();
            PioProgram program = asm.Build();

            PioBlock pio = Pio.Get(0);
            uint offset0 = pio.AddProgram(program);
            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset0, PioStateMachineConfig.FromProgram(program, (int)offset0)
                .ClockFromFrequency(1_000_000f));
            sm.ClearFifos();
            sm.Enabled = true;

            // Native methods that just have to run on the emulated PIO without locking up.
            int ran = 0;
            sm.SetClockDivisor(2f);
            ran++;
            sm.Restart();
            ran++;
            sm.ClockDivRestart();
            ran++;
            sm.Exec((ushort)0xA042); // NOP (mov y, y), executed out of band
            ran++;
            sm.TryPut(1);
            sm.TryPut(2);
            sm.DrainTxFifo();
            ran++;
            pio.ForceIrq(0);
            pio.ClearIrq(0);
            ran++;
            MethodsRun = ran;

            // GetRxLevel after a round trip through the loopback.
            sm.TryPut(7);
            uint got;
            int guard = 0;
            while (!sm.TryGet(out got) && guard++ < 1000)
            {
            }

            RxLevel = (int)sm.GetRxLevel();

            // RemoveProgram reclaim: stop + unclaim the SM, free the slots, re-add reuses offset 0.
            sm.Enabled = false;
            sm.Dispose(); // Unclaim
            pio.RemoveProgram(program, offset0);
            uint offset1 = pio.AddProgram(program);
            RemoveReuseOffset = (int)offset1;

            // Unclaim proof: fill a fresh block, the next claim fails, dispose one, the next succeeds.
            PioBlock pio1 = Pio.Get(1);
            PioStateMachine[] claimed = new PioStateMachine[4];
            for (int i = 0; i < 4; i++)
            {
                claimed[i] = pio1.ClaimStateMachine();
            }

            bool fifthThrew = false;
            try
            {
                pio1.ClaimStateMachine();
            }
            catch
            {
                fifthThrew = true;
            }

            claimed[1].Dispose();

            bool reclaimOk = false;
            try
            {
                pio1.ClaimStateMachine();
                reclaimOk = true;
            }
            catch
            {
            }

            UnclaimWorks = (fifthThrew && reclaimOk) ? 1 : 0;

            // Validation that actually reaches the native checks (the managed layer does not pre-check these).
            try
            {
                pio.RemoveProgram(program, 25); // [25, 28) was never allocated -> native ownership check
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

            // Validation caught at the managed layer (the native overflow guards sit behind these).
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
