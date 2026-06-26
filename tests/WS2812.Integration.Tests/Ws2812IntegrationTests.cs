using System;
using System.Collections.Generic;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using RP2040.TestKit.Boards;
using RP2040.TestKit.Probes;
using Xunit;
using Xunit.Abstractions;

namespace WS2812.Integration.Tests;

/// <summary>
/// Integration tests for the WS2812 showcase. They boot the RP2040 nanoCLR firmware on the
/// RP2040Sharp emulator and run the deployed managed app, using RP2040Sharp.NanoFramework.TestKit:
/// firmware/deployment discovery, the native-checksum guard, and — the headline — driving the
/// emulator to points <em>inside the running CLR</em> (a native InternalCall located by symbol).
/// </summary>
public class Ws2812IntegrationTests
{
    private const int Strip0Pin = 2;
    private const int Strip1Pin = 3;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "WS2812.Sample");

    private readonly ITestOutputHelper _out;
    public Ws2812IntegrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Showcase_drives_two_WS2812_strips_through_the_interpreter()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.Pico.AddPioProbe(0, out PioProbe pio);

        // Run until both strips have their three colours latched into the TX FIFO (or give up).
        for (int i = 0; i < 16000 && (pio.TxOf(0).Count < 3 || pio.TxOf(1).Count < 3); i++)
        {
            clr.Pico.RunMicroseconds(50);
        }

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(pio.TxOf(0).Count >= 3 && pio.TxOf(1).Count >= 3, "strips never received their colours");

        // The exact GRB words the app pushed crossed into each SM's TX FIFO, in order — proof the
        // managed -> PIO path carries the real payload, not just "some" traffic.
        Assert.Equal(new[] { Grb(64, 0, 0), Grb(0, 64, 0), Grb(0, 0, 64) }, First3(pio.TxOf(0)));
        Assert.Equal(new[] { Grb(0, 0, 64), Grb(64, 0, 0), Grb(0, 64, 0) }, First3(pio.TxOf(1)));

        // Both data pins are handed to the PIO (funcsel 6).
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(Strip0Pin));
        Assert.Equal(6u, clr.Pico.Rp2040.IoBank0.GetFuncSel(Strip1Pin));
    }

    // Mirrors the app: GRB packed, then shifted left 8 because the SM clocks out the top 24 bits.
    private static uint Grb(byte r, byte g, byte b) => (uint)(((g << 16) | (r << 8) | b) << 8);
    private static uint[] First3(IReadOnlyList<uint> w) => new[] { w[0], w[1], w[2] };

    [Fact]
    public void Runs_the_CLR_until_the_app_crosses_into_native_AddProgram()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // pio.AddProgram(program) is the showcase's first call across the managed/native boundary.
        // Drive the CLR until the CPU enters that InternalCall's implementation, located by symbol.
        bool reached = clr.RunUntilNativeCall("PioBlock.NativeAddProgram");

        _out.WriteLine($"reached {clr.LastReachedSymbol} at cycle {clr.LastReachedCycle}");

        Assert.True(reached, "the CLR never crossed into PioBlock.NativeAddProgram");
        Assert.Equal("PioBlock.NativeAddProgram", clr.LastReachedSymbol);
    }

    [Fact]
    public void Runs_the_CLR_until_a_managed_static_field_reaches_a_value()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Drive the firmware until the app's managed `static int ColoursSent` reaches 2 — read out of
        // the CLR's heap by walking g_CLR_RT_TypeSystem with a ClrLayout. The field/assembly names are
        // the generated symbols (AppSymbols), so a typo is a compile error, not a raw string.
        bool reached = clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.ColoursSent, v => v.AsInt32 >= 2);

        int value = clr.ReadStaticInt32(AppSymbols.Assembly, AppSymbols.Fields.ColoursSent);
        _out.WriteLine($"ColoursSent = {value} after {clr.InstructionCount} instructions");

        Assert.True(reached, "ColoursSent never reached 2");
        Assert.True(value >= 2);
    }

    [Fact]
    public void Runs_the_CLR_until_the_app_Main_executes()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Drive the firmware until CLR_RT_Thread::Execute_IL runs the app's Main frame, matched by
        // the generated method symbol (no raw "Assembly!Method" string).
        bool reached = clr.RunUntilManagedMethod(AppSymbols.Methods.Main);

        _out.WriteLine($"entered {clr.LastReachedSymbol} at cycle {clr.LastReachedCycle}");
        Assert.True(reached, "the CLR never entered the app's Main");
    }

    [Fact]
    public void Reads_a_managed_instance_array_from_the_heap()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // Run until the app has pushed to both strips, then read the heap-allocated `int[] PerStrip`
        // (a managed instance object) — its length and an element — straight out of the CLR heap.
        clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.ColoursSent, v => v.AsInt32 >= 2);

        Assert.Equal(2u, clr.StaticArrayLength(AppSymbols.Assembly, AppSymbols.Fields.PerStrip));
        Assert.True(clr.ReadStaticArrayInt32(AppSymbols.Assembly, AppSymbols.Fields.PerStrip, 0) >= 1);
    }

    [Fact]
    public void Reads_an_int64_static_and_instance_fields_by_name()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());
        clr.RunUntilStatic(AppSymbols.Assembly, AppSymbols.Fields.ColoursSent, v => v.AsInt32 >= 2);

        // int64 static (its high word is preset to 1) — read the full 64 bits out of the heap cell.
        long heartbeats = clr.ReadStaticInt64(AppSymbols.Assembly, AppSymbols.Fields.Heartbeats);
        Assert.True(heartbeats > 0xFFFFFFFFL, $"int64 high word not read: 0x{heartbeats:X}");

        // Instance fields by name on the heap object held by the static Telemetry reference, addressed
        // through the generated project structure (AppSymbols.Types.Stats).
        int total = clr.ReadInstanceInt32(AppSymbols.Assembly, AppSymbols.Fields.Telemetry,
            AppSymbols.Types.Stats.Name, AppSymbols.Types.Stats.Fields.Total);
        int iterations = clr.ReadInstanceInt32(AppSymbols.Assembly, AppSymbols.Fields.Telemetry,
            AppSymbols.Types.Stats.Name, AppSymbols.Types.Stats.Fields.Iterations);

        _out.WriteLine($"Heartbeats=0x{heartbeats:X} Telemetry.Total={total} Telemetry.Iterations={iterations}");
        Assert.True(total >= 2);
        Assert.True(iterations >= 1);
    }

    [Fact]
    public void Deployment_native_checksums_match_the_firmware()
    {
        NanoApp app = App();
        Firmware().AssertCompatible(app); // must not throw
    }

    [Fact]
    public void Checksum_guard_rejects_a_drifted_library()
    {
        NanoApp app = App();

        // A firmware whose manifest claims a different library checksum (simulating a rebuilt library
        // whose InternalCall surface drifted from the flashed firmware).
        string tmp = Directory.CreateTempSubdirectory("nf-fw").FullName;
        foreach (string bin in Directory.GetFiles(FirmwareDir, "*.bin"))
        {
            File.Copy(bin, Path.Combine(tmp, Path.GetFileName(bin)));
        }

        File.WriteAllText(Path.Combine(tmp, "firmware.manifest.json"),
            "{ \"nativeChecksums\": { \"nanoFramework.Hardware.Rpi\": \"0xDEADBEEF\" } }");

        var ex = Assert.Throws<NanoChecksumMismatchException>(
            () => NanoFirmware.FromDirectory(tmp).AssertCompatible(app));
        Assert.Equal("nanoFramework.Hardware.Rpi", ex.Assembly);
        Assert.Equal(0xDEADBEEFu, ex.FirmwareChecksum);
    }
}
