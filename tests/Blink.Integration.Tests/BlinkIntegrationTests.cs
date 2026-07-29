using System;
using System.Collections.Generic;
using System.IO;
using RP2350Sharp.NanoFramework.TestKit;
using RP2350.TestKit;
using RP2350.TestKit.Probes;
using Xunit;

namespace Blink.Integration.Tests;

/// <summary>
/// Boots the Blink sample on the emulated RP2350 (Cortex-M33) nanoCLR and watches the PIO peripheral
/// drive the LED: the alternating levels crossing into the TX FIFO and the pin actually toggling —
/// which an on-device managed test can't see — plus the native-checksum guard. (Test kit BUSL; sample
/// + PIO library MIT.)
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
        clr.Sim.AddPioProbe(0, out PioProbe pio);
        // A logic analyzer on the LED pad records the PIO-driven edges (the RP2350 machine already owns
        // Pio0.WriteGpioPins for the GPIO mux, so we observe the pad instead of hooking the delegate).
        LogicAnalyzer led = clr.Sim.AddLogicAnalyzer(LedPin);

        // Toggles are 500 ms apart (Thread.Sleep), so step in coarse chunks until four have landed.
        for (int i = 0; i < 8000 &&
             (pio.TxOf(0).Count < 4 || led.RisingEdges(LedPin) == 0 || led.FallingEdges(LedPin) == 0); i++)
        {
            clr.Sim.RunMicroseconds(500);
        }

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");

        // The app pushes 1,0,1,0 — exactly the levels that crossed into the TX FIFO — and the PIO drives
        // the LED pin both ways.
        Assert.Equal(new uint[] { 1, 0, 1, 0 }, First(pio.TxOf(0), 4));
        Assert.Equal(6u, clr.Sim.Machine.IoBank0.GetFuncSel(LedPin));
        Assert.True(led.RisingEdges(LedPin) > 0 && led.FallingEdges(LedPin) > 0,
            "the PIO never drove the LED pin both ways");
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
