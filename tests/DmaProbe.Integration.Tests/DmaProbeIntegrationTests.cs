using System;
using System.IO;
using RP2350Sharp.NanoFramework.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace DmaProbe.Integration.Tests;

/// <summary>
/// Boots the DmaProbe sample on the emulated nanoCLR and checks the DMA-paced PIO FIFO transfers the
/// native binding performs (sm.Write / sm.Read, backed by a DREQ-paced DMA channel). The sample records
/// each transfer's word count in a static field via NfDmaChannelGetCounter (CH_TRANS_COUNT); this test
/// reads those fields, so the counts are asserted deterministically without an interactive debugger.
/// </summary>
public class DmaProbeIntegrationTests(ITestOutputHelper output)
{
    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "DmaProbe.Sample");

    private static int Read(NanoClrHarness clr, string field) =>
        clr.ReadStaticInt32(AppSymbols.Assembly, field);

    private static bool TryRead(NanoClrHarness clr, string field, out int value)
    {
        try { value = Read(clr, field); return true; }
        catch (InvalidOperationException) { value = int.MinValue; return false; }
    }

    [Fact]
    public void Dma_paced_pio_loopback_transfers_the_full_batch()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // The sample's timeouts (TimeoutMs=1000 on the overflow write and the empty-RX read) take seconds
        // of emulated time, so give it a generous window and stop once its last measurement lands.
        var written = -1; var readBack = -1; var roundTrip = 0; var readStalled = -1; var writeStalled = -1;
        var done = false;
        var initialized = false;
        for (var i = 0; i < 120_000 && !clr.IsLockedUp && !done; i++)
        {
            clr.Sim.RunMicroseconds(100);
            if (TryRead(clr, AppSymbols.Fields.Written, out var w)) written = w;
            if (TryRead(clr, AppSymbols.Fields.ReadBack, out var r)) readBack = r;
            if (TryRead(clr, AppSymbols.Fields.RoundTripOk, out var rt)) roundTrip = rt;
            if (TryRead(clr, AppSymbols.Fields.WriteStalled, out var ws)) writeStalled = ws;
            if (TryRead(clr, AppSymbols.Fields.ReadStalled, out var rs))
            {
                // Static fields read 0 until the class constructor sets them to -1.
                initialized |= rs == -1;
                readStalled = rs;
                done = initialized && rs != -1;
            }
        }

        output.WriteLine($"Written={written} ReadBack={readBack} RoundTripOk={roundTrip} WriteStalled={writeStalled} ReadStalled={readStalled} done={done} locked={clr.IsLockedUp}");
        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(done, $"the DmaProbe sample never finished (Written={written} ReadBack={readBack} WriteStalled={writeStalled})");

        // A 4-word DMA-paced PIO loopback must move all 4 words each way; the round trip must match; and a
        // read over the deliberately-emptied RX FIFO must transfer nothing.
        Assert.Equal(4, written);
        Assert.Equal(4, readBack);
        Assert.Equal(1, roundTrip);
        Assert.Equal(0, readStalled);
    }
}
