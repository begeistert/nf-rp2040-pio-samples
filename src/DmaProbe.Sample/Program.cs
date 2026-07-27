//
// Diagnostic probe — DMA-backed Read/Write, kept small enough to run end to end in about a second.
//
// Milestones are distinct no-argument natives, so a breakpoint on the address says which stage passed
// without anyone reading the managed heap:
//
//   Restart()         full round trip: every word written came back unchanged and in order
//   ClockDivRestart() short transfers against a stopped state machine report what actually moved
//   DrainTxFifo()     repeated timeouts hand every channel back to the pool
//
// The short-transfer stage is the one that catches a regression: a Write into a state machine that
// consumes nothing must report the words that fitted in the FIFO, and a Read from an empty RX must
// report zero. A stage that reports the requested count is reading the DMA counter after the abort
// has already zeroed it.
//
using System.Threading;
using nanoFramework.Hardware.Pico.Pio;

namespace DmaProbeSample
{
    public class Program
    {
        private const uint SentinelBase = 0xDA000000;
        private const int Batch = 4;
        private const int Overflow = 16;
        private const int TimeoutMs = 1000;
        private const int ShortTimeoutMs = 5;
        private const int LeakRounds = 20;

        public static int Written = -1;
        public static int ReadBack = -1;
        public static int RoundTripOk;
        public static int WriteStalled = -1;
        public static int ReadStalled = -1;
        public static int PoolIntact;

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

            PioStateMachine sm = pio.ClaimStateMachine();
            sm.Init(offset, PioStateMachineConfig.FromProgram(program, (int)offset)
                .ClockFromFrequency(1_000_000f));
            sm.ClearFifos();
            sm.Enabled = true;

            uint[] tx = new uint[Batch];
            uint[] rx = new uint[Batch];
            for (int i = 0; i < Batch; i++)
            {
                tx[i] = SentinelBase + (uint)i;
            }

            try
            {
                Written = sm.Write(tx, 0, Batch, TimeoutMs);
                ReadBack = sm.Read(rx, 0, Batch, TimeoutMs);
            }
            catch
            {
                sm.Restart();
            }

            for (int i = 0; i < Written; i++)
            {
                pio.ForceIrq(0);
            }

            for (int i = 0; i < ReadBack; i++)
            {
                pio.ClearIrq(0);
            }

            bool ok = true;
            for (int i = 0; i < Batch && ok; i++)
            {
                ok = rx[i] == tx[i];
            }

            if (ok)
            {
                RoundTripOk = 1;
                sm.Restart();
            }

            sm.Enabled = false;
            sm.ClearFifos();

            uint[] overflow = new uint[Overflow];
            WriteStalled = sm.Write(overflow, 0, Overflow, TimeoutMs);

            uint[] drain = new uint[Batch];
            ReadStalled = sm.Read(drain, 0, Batch, TimeoutMs);

            if (WriteStalled >= 0 && WriteStalled < Overflow)
            {
                sm.ClockDivRestart();
            }

            if (ReadStalled == 0)
            {
                sm.Exec(0xA042);
            }

            bool intact = true;
            for (int i = 0; i < LeakRounds && intact; i++)
            {
                intact = sm.Read(drain, 0, Batch, ShortTimeoutMs) == 0;
            }

            if (intact)
            {
                PoolIntact = 1;
                sm.DrainTxFifo();
            }

            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
