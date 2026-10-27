using Ryujinx.HLE.HOS.Services.Hid.Netplay;
using System.Globalization;

internal static class DiagnosticTests
{
    public static void Run()
    {
        const ulong main = 0x8504000;
        List<ulong> reads = [];
        RyuSyncBattleClock clock = RyuSyncBattleClock.Read(address => { reads.Add(address); return 1; }, main);
        ulong[] expected = [main + 0x64548A0, main + 0x64548A4, main + 0x64548A8,
            main + 0x64548AC, main + 0x64548B0, main + 0x4319CC0];
        if (!reads.SequenceEqual(expected)) throw new Exception("Diagnostic memory reads escaped verified fields");
        RyuSyncDiagnostics capture = new("test-session", RyuSyncRole.Host);
        if (capture.Export().Contains("\n0|")) throw new Exception("Empty capture fabricated a sample");
        for (int i = 0; i < RyuSyncDiagnostics.Capacity + 7; i++)
        {
            capture.Add(new(i, true, default, default, clock, clock));
        }
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            string text = capture.Export();
            string[] samples = text.Split('\n').Where(line => line.Length > 0 && char.IsAsciiDigit(line[0])).ToArray();
            if (samples.Length != RyuSyncDiagnostics.Capacity || !samples[0].StartsWith("7|1|") ||
                !samples[^1].StartsWith($"{(RyuSyncDiagnostics.Capacity + 6).ToString(CultureInfo.InvariantCulture)}|1|"))
            {
                throw new Exception("Diagnostic retention/order/culture handling failed");
            }
            if (text != capture.Export()) throw new Exception("Export consumed the capture");
            capture.Fail("read failed");
            if (!capture.Export().Contains("error=read failed")) throw new Exception("Capture hid a read failure");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Console.WriteLine("PASS: diagnostic reads are bounded; retention, ordering, repeat export and failure reporting are preserved.");
    }
}
