using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
using Xunit;

namespace Fade.Integration.Tests;

/// <summary>
/// Boots the Fade sample on the emulated RP2040 nanoCLR and watches the PIO peripheral: the PWM duty
/// ramp crossing into the TX FIFO and the SM driving the LED pin both ways — which an on-device managed
/// test can't see — plus the native-checksum guard. (Test kit BUSL; sample + PIO library MIT.)
/// </summary>
public class FadeIntegrationTests
{
    private const int LedPin = 25;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Fade.Sample");

    [Fact]
    public void Streams_the_duty_ramp_and_PWMs_the_pin()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        int hi = 0, lo = 0;
        clr.Pico.Rp2040.Pio0.WriteGpioPins = (value, mask) =>
        {
            if ((mask & (1u << LedPin)) != 0) { if (((value >> LedPin) & 1) != 0) hi++; else lo++; }
        };

        for (int i = 0; i < 30000 && (pio.TxOf(0).Count < 5 || hi == 0 || lo == 0); i++)
            clr.Pico.RunMicroseconds(100);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");

        // The app ramps the duty 0,1,2,3,4… straight into the TX FIFO, and the SM PWMs the LED pin.
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, First(pio.TxOf(0), 5));
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(LedPin));
        Assert.True(hi > 0 && lo > 0, "the SM never PWM'd the LED pin");
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
