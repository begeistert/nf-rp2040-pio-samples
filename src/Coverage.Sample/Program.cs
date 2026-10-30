//
// Sample — coverage harness for the PIO native interop. Not a "pretty" demo: it drives the FIFO
// loopback program and exercises the native methods the other showcases never reach (RemoveProgram,
// Exec, Restart, ClockDivRestart, DrainTxFifo, Unclaim via Dispose, RxLevel, ClockDivisor,
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
        public const uint ClockSentinel = 0xC0DE;     // echoes after ClockDivisor/ClockDivRestart
        public const uint ReuseSentinel = 0xABCDABCD; // echoes after RemoveProgram + re-add
        public const uint DmaSentinelBase = 0xDA000000; // first word of the DMA round-trip batch
        public const uint BlockingSentinel = 0xB10CB10C; // round-tripped with the blocking Put/Get pair

        private const int DmaBatch = 4;           // fits the FIFO, so a healthy round trip never stalls
        private const int DmaOverflow = 16;       // deeper than any FIFO, so the write has to stall
        private const int DmaTimeoutMs = 100;
        private const int DmaShortTimeoutMs = 5;
        private const int DmaLeakRounds = 20;     // more rounds than the RP2040 has DMA channels
        private const int DisposeDelayMs = 200;
        private const int TickIntervalMs = 10;
        private const int DisposeReadTimeoutMs = 5000;

        public static int RemoveReuseOffset = -1; // second AddProgram offset after RemoveProgram (expect 0)
        public static int UnclaimWorks;           // 1 if exhaust/dispose/re-claim proves Unclaim frees a slot
        public static int RxLevelSeen = -1;       // RxLevel reading while words sit in RX (expect > 0)
        public static int RemoveEchoOk;           // 1 if the re-added program echoed its sentinel
        public static int RemoveUnownedThrew;     // native: RemoveProgram of a range that was never allocated
        public static int InitBadOffsetThrew;     // native: Init with an offset outside 0..31
        public static int PinDirsBadThrew;        // managed backstop: SetConsecutivePinDirs basePin > 47
        public static int InitGpioBadThrew;       // managed backstop: InitGpio pin > 47
        public static int FromEncodedOptionsOk;   // 1 if FromEncoded(options) carried every metadata field

        // DMA-backed Read/Write. The interesting numbers are the short transfers: a stalled state
        // machine must report the words that actually moved, never the count that was asked for.
        public static int DmaWritten = -1;        // Write of a full round-trip batch (expect batch size)
        public static int DmaRead = -1;           // Read of that same batch (expect batch size)
        public static int DmaRoundTripOk;         // 1 if every word came back unchanged and in order
        public static int DmaWriteStalled = -1;   // Write into a state machine that consumes nothing
        public static int DmaReadStalled = -1;    // Read from an empty RX FIFO (expect 0)
        public static int DmaSurvivesTimeouts;    // 1 if repeated timeouts leave the channel pool intact
        public static int DisposeInFlightOk;      // 1 if disposing mid-transfer unwound without hanging

        // Firmware-reported topology, so a sample never has to hardcode a block index or a pin ceiling.
        public static int MinIndexSeen = -1;
        public static int BlockCountSeen = -1;
        public static int MaxPinSeen = -1;
        public static int TopologyOk;             // 1 if the three constants are coherent with each other
        public static int SmIndexEnumOk;          // 1 if StateMachine(enum) round-trips through Index

        // Blocking Put/Get. These park the calling thread on the FIFO interrupt in native code; the
        // point of the checks below is that parking is not the same as freezing the interpreter.
        public static int BlockingRoundTripOk;    // 1 if Put/Get moved a word through the loopback
        public static int BlockingPutTimedOut;    // 1 if Put on a stalled SM gave up instead of hanging
        public static int TicksWhileParked = -1;  // background thread progress during that parked Put

        public static int Done;                   // set last, so the test knows recording finished

        // The smallest program that uses both FIFOs: TX -> OSR -> ISR -> RX. Round-tripped through the
        // FromEncoded(options) overload so the whole harness runs end-to-end on a precompiled-style program.
        private static PioProgram BuildLoopback()
        {
            var asm = new PioAssembler();
            asm.WrapTarget();
            asm.Pull(ifEmpty: false, block: true);
            asm.Mov(DestinationOperand.InputShiftRegister, SourceOperand.OutputShiftRegister);
            asm.Push(ifFull: false, block: true);
            asm.Wrap();
            PioProgram built = asm.Build();
            return PioProgram.FromEncoded(built.Instructions, built.WrapTarget, built.Wrap, new PioProgramOptions
            {
                Version = built.Version,
                Origin = built.Origin,
                SideSetCount = built.SideSetCount,
                SideSetOptional = built.SideSetOptional,
                SideSetPinDirs = built.SideSetPinDirs,
                OutShiftDir = built.OutShiftDir,
                AutoPull = built.AutoPull,
                PullThreshold = built.PullThreshold,
                InShiftDir = built.InShiftDir,
                AutoPush = built.AutoPush,
                PushThreshold = built.PushThreshold,
            });
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

            PioBlock pio = Pio.Get(Pio.MinIndex);
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
            enc.Set(DestinationOperand.RegisterX, (int)ExecSentinel);
            enc.In(SourceOperand.RegisterX, 32);
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

            // --- RxLevel (probe-observed cross-check) ---
            // Push three words without draining so they pile up in RX, read the native level, then drain
            // them (the probe pulls all three). RxLevelSeen > 0 means the native FLEVEL read saw the words
            // the probe then confirms crossed the FIFO.
            sm.TryPut(0x101);
            sm.TryPut(0x202);
            sm.TryPut(0x303);
            int spin = 0;
            while (sm.RxLevel < 3 && spin++ < 4000)
            {
            }

            RxLevelSeen = (int)sm.RxLevel;
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
            //
            // The loopback program pulls explicitly, so autopull is off and the drain has to consume the
            // FIFO with PULL rather than OUT NULL. Getting that wrong leaves both words in TX, and the
            // re-enabled state machine echoes them ahead of the marker — which is what this catches.
            sm.Enabled = false;
            sm.TryPut(DrainDropped1);
            sm.TryPut(DrainDropped2);
            sm.DrainTxFifo();
            sm.Enabled = true;
            Echo(sm, DrainKept);

            // --- ClockDivisor + ClockDivRestart (smoke: not FIFO-observable) ---
            // The probe timestamps the firmware's TryPut/TryGet, not the PIO clock, so a clock change can't
            // be proven through the FIFO here. We only prove the SM keeps working after re-clocking by
            // echoing a sentinel.
            sm.ClockDivisor = 2f;
            sm.ClockDivRestart();
            Echo(sm, ClockSentinel);

            // --- ClearIrq (smoke) ---
            // No managed IRQ-flag read-back, so this only proves Force/Clear run without locking up.
            pio.ForceIrq(0);
            pio.ClearIrq(0);

            // --- Read/Write round trip (probe-observed) ---
            // Both directions at once: Write feeds the TX FIFO through a DMA channel paced by the TX
            // request, the loopback program moves each word OSR -> ISR, and Read drains RX through a
            // second channel paced by the RX request. Batch is FIFO-sized so the round trip completes
            // without the state machine ever stalling.
            sm.ClearFifos();
            uint[] txBatch = new uint[DmaBatch];
            uint[] rxBatch = new uint[DmaBatch];
            for (int i = 0; i < DmaBatch; i++)
            {
                txBatch[i] = DmaSentinelBase + (uint)i;
            }

            DmaWritten = sm.Write(txBatch, 0, DmaBatch, DmaTimeoutMs);
            DmaRead = sm.Read(rxBatch, 0, DmaBatch, DmaTimeoutMs);

            bool roundTripOk = DmaWritten == DmaBatch && DmaRead == DmaBatch;
            for (int i = 0; i < DmaBatch && roundTripOk; i++)
            {
                roundTripOk = rxBatch[i] == txBatch[i];
            }

            DmaRoundTripOk = roundTripOk ? 1 : 0;

            // --- Short transfers (the timeout accounting) ---
            // With the state machine stopped nothing drains TX and nothing fills RX, so both calls have
            // to time out. What matters is the count they report: the write moves whatever fits in the
            // FIFO and then stalls, the read moves nothing at all. A transfer that reports the full
            // count here is reading the DMA counter after the abort has already zeroed it.
            sm.Enabled = false;
            sm.ClearFifos();

            uint[] overflow = new uint[DmaOverflow];
            DmaWriteStalled = sm.Write(overflow, 0, DmaOverflow, DmaTimeoutMs);

            uint[] drain = new uint[DmaBatch];
            DmaReadStalled = sm.Read(drain, 0, DmaBatch, DmaTimeoutMs);

            // --- Channel pool after repeated timeouts ---
            // Every timed-out transfer must hand its channel back. There are only 12 of them on the
            // RP2040, so a leak turns into an outright failure well inside this loop instead of the
            // steady stream of short reads a working release path produces.
            bool poolIntact = true;
            for (int i = 0; i < DmaLeakRounds && poolIntact; i++)
            {
                try
                {
                    poolIntact = sm.Read(drain, 0, DmaBatch, DmaShortTimeoutMs) == 0;
                }
                catch
                {
                    poolIntact = false;
                }
            }

            DmaSurvivesTimeouts = poolIntact ? 1 : 0;
            sm.ClearFifos();

            // --- Dispose while a transfer is in flight ---
            // The riskiest unwind in the binding: the DMA is writing into a bounce buffer that Dispose
            // is about to release, so the release path has to abort the channel before it frees. A
            // second state machine takes the hit so the harness can carry on afterwards, and the read
            // is given far longer than the disposer waits, so the dispose genuinely lands mid-transfer.
            PioStateMachine smInFlight = pio.ClaimStateMachine();
            smInFlight.Init(offset0, PioStateMachineConfig.FromProgram(program, (int)offset0)
                .ClockFromFrequency(1_000_000f));
            smInFlight.ClearFifos();

            var disposer = new Thread(() =>
            {
                Thread.Sleep(DisposeDelayMs);
                smInFlight.Dispose();
            });
            disposer.Start();

            try
            {
                smInFlight.Read(drain, 0, DmaBatch, DisposeReadTimeoutMs);
            }
            catch
            {
                // A disposed state machine is a legitimate outcome here; a hang or a corrupt heap is not.
            }

            disposer.Join();
            DisposeInFlightOk = 1;

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
            PioBlock pioB = Pio.Get(Pio.MinIndex + 1);
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

            // FromEncoded(options) round-trip: every metadata field must survive into the program and the
            // 3-arg overload must still default them. Not probe-observable, so checked against the properties.
            ushort[] feInstr = new ushort[] { 0xA042 };
            PioProgramOptions opts = new PioProgramOptions
            {
                Version = PioVersion.Rp2350,
                Origin = 5,
                SideSetCount = 2,
                SideSetOptional = true,
                SideSetPinDirs = true,
                OutShiftDir = ShiftDirection.Left,
                AutoPull = true,
                PullThreshold = 8,
                InShiftDir = ShiftDirection.Left,
                AutoPush = true,
                PushThreshold = 16,
            };
            PioProgram fe = PioProgram.FromEncoded(feInstr, 0, 0, opts);
            PioProgram feDefault = PioProgram.FromEncoded(feInstr, 0, 0);
            FromEncodedOptionsOk =
                (fe.Version == PioVersion.Rp2350 && fe.Origin == 5 && fe.SideSetCount == 2 && fe.SideSetOptional &&
                 fe.SideSetPinDirs && fe.OutShiftDir == ShiftDirection.Left && fe.AutoPull && fe.PullThreshold == 8 &&
                 fe.InShiftDir == ShiftDirection.Left && fe.AutoPush && fe.PushThreshold == 16 &&
                 !feDefault.AutoPull && feDefault.PullThreshold == 32 && feDefault.SideSetCount == 0) ? 1 : 0;

            // --- Firmware-reported topology ---
            MinIndexSeen = Pio.MinIndex;
            BlockCountSeen = Pio.BlockCount;
            MaxPinSeen = Pio.MaxPin;
            TopologyOk = (MinIndexSeen >= 0 && MinIndexSeen < BlockCountSeen && MaxPinSeen > 0) ? 1 : 0;

            // Claiming a named index only means something once that index is known to be free, so the
            // harness releases one it already holds and asks for exactly that one back.
            PioStateMachineIndex freed = sm2.Index;
            sm2.Dispose();
            PioStateMachine smByIndex = pio.ClaimStateMachine(freed);
            SmIndexEnumOk = smByIndex.Index == freed ? 1 : 0;

            // --- Blocking Put/Get through the loopback ---
            sm1.ClearFifos();
            sm1.Enabled = true;
            sm1.Put(BlockingSentinel);
            BlockingRoundTripOk = sm1.Get() == BlockingSentinel ? 1 : 0;

            // --- Blocking Put against a state machine that consumes nothing ---
            // The FIFO fills, the call parks on the TX interrupt and eventually gives up. A background
            // thread counts ticks throughout: if the native wait spun instead of parking, the CLR would
            // be frozen for the whole budget and this counter would barely move.
            sm1.Enabled = false;
            sm1.ClearFifos();

            int ticks = 0;
            bool ticking = true;
            var ticker = new Thread(() =>
            {
                while (ticking)
                {
                    ticks++;
                    Thread.Sleep(TickIntervalMs);
                }
            });
            ticker.Start();

            try
            {
                for (int i = 0; i < 16; i++)
                {
                    sm1.Put(0xF10F0000 + (uint)i);
                }
            }
            catch
            {
                BlockingPutTimedOut = 1;
            }

            ticking = false;
            TicksWhileParked = ticks;

            sm1.ClearFifos();

            Done = 1;

            while (true)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
