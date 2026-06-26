using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
using Xunit;

namespace Echo.Integration.Tests;

/// <summary>
/// Boots the Echo sample on the emulated RP2040 nanoCLR and watches the PIO loopback: the words the app
/// pushes coming back unchanged through the TX/RX FIFOs, plus the new FLEVEL/PC native reads and the
/// native-checksum guard. (Test kit BUSL; sample + PIO library MIT.)
/// </summary>
public class EchoIntegrationTests
{
    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Echo.Sample");

    [Fact]
    public void Echoes_each_word_back_unchanged_through_both_FIFOs()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        for (int i = 0; i < 8000 && pio.RxOf(0).Count < 3; i++)
            clr.Pico.RunMicroseconds(100);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(pio.RxOf(0).Count >= 3, "no words came back from the loopback");

        // The app pushes 1,2,3…; the loopback returns each unchanged, so TX and RX carry the same words.
        Assert.Equal(new uint[] { 1, 2, 3 }, First(pio.TxOf(0), 3));
        Assert.Equal(new uint[] { 1, 2, 3 }, First(pio.RxOf(0), 3));
    }

    [Fact]
    public void Reads_back_FIFO_level_and_program_counter()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        for (int i = 0; i < 8000 && pio.RxOf(0).Count < 2; i++)
            clr.Pico.RunMicroseconds(100);

        // The app records what the new native FLEVEL/PC accessors returned while looping.
        int pc = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.LastPc);
        int maxTx = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.MaxTxLevel);

        Assert.InRange(pc, 0, 2);    // PC stays within the 3-instruction loopback program
        Assert.InRange(maxTx, 0, 8); // a valid TX FIFO level
    }

    [Fact]
    public void Deployment_is_compatible_with_the_firmware()
    {
        Firmware().AssertCompatible(App());
    }

    private static uint[] First(IReadOnlyList<uint> w, int n)
    {
        var r = new uint[n];
        for (int i = 0; i < n; i++) r[i] = w[i];
        return r;
    }
}
