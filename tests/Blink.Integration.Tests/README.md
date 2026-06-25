# Blink.Integration.Tests

Integration test for the **Blink** sample. It boots the deployed app on the RP2040 nanoCLR running in the
**RP2040Sharp** emulator, then drives the CLR until the app exercises the PIO — no physical board needed.

Uses the `RP2040Sharp.TestKit.NanoFramework` NuGet test kit (boot the nanoCLR, generate strongly-typed
app symbols, walk managed state, check native checksums). The test kit is **BUSL-1.1**; the sample app
and the PIO library are MIT.

Run it:

```bash
dotnet test
```
