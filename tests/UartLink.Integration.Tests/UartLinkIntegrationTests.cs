using System;
using System.IO;
using RP2040Sharp.NanoFramework.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace UartLink.Integration.Tests;

/// <summary>
/// Boots the UartLink sample and closes the link in software: whatever the transmit state machine
/// drives on the TX pin is fed straight back into the RX pin, so the receive program has to decode,
/// bit by bit, exactly what the transmit program encoded. On real hardware the same image runs on two
/// boards; here one board talks to itself over an exact wire, which is what makes the run
/// deterministic and CI-able.
///
/// What this covers that two boards cannot cover cheaply: it is repeatable, it needs no wiring, and a
/// regression in the sampling point, the stop-bit check or the frame handling fails the build. What it
/// deliberately does NOT cover: both ends share the emulated clock, so a wrong divider is wrong by the
/// same amount at each end and cancels out. That is the one thing only two independent crystals can
/// prove, and it is why the hardware run still matters. (Test kit BUSL; sample + PIO library MIT.)
/// </summary>
public class UartLinkIntegrationTests(ITestOutputHelper output)
{
    private const int TxPin = 0;
    private const int RxPin = 1;
    private const int FramesToSend = 64;

    private static string FirmwareDir => Path.Combine(AppContext.BaseDirectory, "firmware");
    private static string PeDir => Path.Combine(AppContext.BaseDirectory, "pe");

    private static NanoFirmware Firmware() => NanoFirmware.FromDirectory(FirmwareDir);
    private static NanoApp App() => NanoApp.FromPeDirectory(PeDir, appAssemblyName: "UartLink.Sample");

    private static bool TryRead(NanoClrHarness clr, string field, out int value)
    {
        try { value = clr.ReadStaticInt32(AppSymbols.Assembly, field); return true; }
        catch (InvalidOperationException) { value = int.MinValue; return false; }
    }

    [Fact]
    public void Receives_every_frame_the_transmitter_sends()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        // The exact wire. WriteGpioPins fires the moment the state machine drives the line, so the
        // receiver sees the same edges at the same cycle rather than a version resampled by the test.
        clr.Pico.Rp2040.Pio0.WriteGpioPins = (value, mask) =>
        {
            if ((mask & (1u << TxPin)) != 0)
            {
                clr.Pico.Gpio[RxPin].ForceInput(((value >> TxPin) & 1) != 0);
            }
        };

        int sent = 0, ok = 0, corrupt = 0, lost = 0, timeouts = 0, txDone = 0;

        // A 35-byte frame at 115200 baud takes about 3 ms of emulated time and the sample sleeps
        // between frames, so the run is under a second. The wire is a callback, not a sampler, so the
        // step size here costs nothing in fidelity.
        for (var i = 0; i < 200_000 && !clr.IsLockedUp; i++)
        {
            clr.Pico.RunMicroseconds(50);

            if (i % 200 != 0)
            {
                continue;
            }

            TryRead(clr, AppSymbols.Fields.FramesSent, out sent);
            TryRead(clr, AppSymbols.Fields.FramesOk, out ok);
            TryRead(clr, AppSymbols.Fields.FramesCorrupt, out corrupt);
            TryRead(clr, AppSymbols.Fields.FramesLost, out lost);
            TryRead(clr, AppSymbols.Fields.Timeouts, out timeouts);
            TryRead(clr, AppSymbols.Fields.TxDone, out txDone);

            if (txDone == 1 && ok >= FramesToSend)
            {
                break;
            }
        }

        output.WriteLine(
            $"sent={sent} ok={ok} corrupt={corrupt} lost={lost} timeouts={timeouts} " +
            $"txDone={txDone} locked={clr.IsLockedUp}");

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.Equal(1, txDone);
        Assert.Equal(FramesToSend, sent);

        // Every frame the transmitter put on the line came back with its sequence number in order and
        // its checksum intact. A wrong sampling point shows up here as corruption, not as silence.
        Assert.Equal(FramesToSend, ok);
        Assert.Equal(0, corrupt);
        Assert.Equal(0, lost);
    }

    [Fact]
    public void Drives_the_tx_pin_from_the_pio_and_leaves_rx_an_input()
    {
        using var clr = NanoClrHarness.Boot(Firmware(), App());

        for (var i = 0; i < 100_000 && !clr.IsLockedUp; i++)
        {
            clr.Pico.RunMicroseconds(50);

            if (clr.Pico.Gpio[TxPin].IsPioOutput && !clr.Pico.Gpio[RxPin].IsOutput)
            {
                break;
            }
        }

        Assert.False(clr.IsLockedUp, "nanoCLR locked up");
        Assert.True(clr.Pico.Gpio[TxPin].IsPioOutput, "the TX pin was never handed to the PIO");
        Assert.False(clr.Pico.Gpio[RxPin].IsOutput, "the RX pin must stay an input");
    }

    [Fact]
    public void Deployment_native_checksums_match_the_firmware()
    {
        Firmware().AssertCompatible(App());
    }
}
