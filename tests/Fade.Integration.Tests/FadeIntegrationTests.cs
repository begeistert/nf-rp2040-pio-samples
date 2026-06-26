using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Probes;
using Xunit;
using Xunit.Abstractions;

namespace Fade.Integration.Tests;

/// <summary>
/// Integration test for the Fade sample. Boots the RP2040 nanoCLR firmware on the RP2040Sharp
/// emulator, deploys the managed app, and drives the CLR until the app has streamed PWM duty steps
/// to a PIO state machine — validating the assembler + native interop end to end (the test kit is
/// the author's BUSL offering; the sample app and the PIO library are MIT).
/// </summary>
public class FadeIntegrationTests
{
    private const int LedPin = 25;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "Fade.Sample");

    private readonly ITestOutputHelper _out;
    public FadeIntegrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Streams_PWM_duty_steps_to_a_PIO_state_machine()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Run the deployed C# app until it has streamed a couple of PWM duty levels into the FIFO.
        bool reached = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.Steps, v => v.AsInt32 >= 2);
        int steps = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.Steps);

        _out.WriteLine($"Steps = {steps} after {clr.InstructionCount} instructions");
        Assert.True(reached, "the fade app never streamed a duty step");
        Assert.True(steps >= 2);
    }

    [Fact]
    public void Streams_the_duty_ramp_into_the_TX_FIFO()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        for (int i = 0; i < 8000 && pio.TxOf(0).Count < 5; i++)
            clr.Pico.RunMicroseconds(100);

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(pio.TxOf(0).Count >= 5, "no duty steps reached the PIO FIFO");

        // The app ramps the duty 0,1,2,3,4… straight into the SM's TX FIFO.
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, First(pio.TxOf(0), 5));
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(LedPin));
    }

    [Fact]
    public void Deployment_native_checksums_match_the_firmware()
    {
        NanoApp app = App();
        Firmware().AssertCompatible(app); // must not throw
    }

    private static uint[] First(IReadOnlyList<uint> w, int n)
    {
        var r = new uint[n];
        for (int i = 0; i < n; i++) r[i] = w[i];
        return r;
    }
}
