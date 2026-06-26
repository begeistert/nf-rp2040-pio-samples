using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
using Xunit;

namespace Blink.Integration.Tests;

/// <summary>
/// Boots the Blink sample on the emulated RP2040 nanoCLR and watches the PIO peripheral drive the LED:
/// the alternating levels crossing into the TX FIFO and the pin actually toggling — which an on-device
/// managed test can't see — plus the native-checksum guard. (Test kit BUSL; sample + PIO library MIT.)
/// </summary>
public class BlinkIntegrationTests
{
    private const int LedPin = 25;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Blink.Sample");

    [Fact]
    public void Toggles_the_LED_pin_through_a_PIO_state_machine()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        int hi = 0, lo = 0;
        clr.Pico.Rp2040.Pio0.WriteGpioPins = (value, mask) =>
        {
            if ((mask & (1u << LedPin)) != 0) { if (((value >> LedPin) & 1) != 0) hi++; else lo++; }
        };

        // Toggles are 500 ms apart (Thread.Sleep), so step in coarse chunks until four have landed.
        for (int i = 0; i < 8000 && (pio.TxOf(0).Count < 4 || hi == 0 || lo == 0); i++)
            clr.Pico.RunMicroseconds(500);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");

        // The app pushes 1,0,1,0 — exactly the levels that crossed into the TX FIFO — and the PIO drives
        // the LED pin both ways.
        Assert.Equal(new uint[] { 1, 0, 1, 0 }, First(pio.TxOf(0), 4));
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(LedPin));
        Assert.True(hi > 0 && lo > 0, "the PIO never drove the LED pin both ways");
    }

    [Fact]
    public void Deployment_native_checksums_match_the_firmware()
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
