using System.Diagnostics;

namespace MemPalace.IntegrationTests;

/// <summary>
/// Lightweight test harness that prints coloured PASS / FAIL lines and collects results.
/// </summary>
internal sealed class TestHarness
{
    private readonly List<(string name, bool passed, string detail, TimeSpan elapsed)> _results = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();

    // ── assertion helpers ─────────────────────────────────────────────────────

    public void Assert(string name, bool condition, string? failDetail = null)
    {
        Record(name, condition, condition ? "" : failDetail ?? "assertion failed", TimeSpan.Zero);
    }

    public async Task RunAsync(string name, Func<Task<string>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await action();
            sw.Stop();
            Record(name, true, detail, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Record(name, false, ex.Message, sw.Elapsed);
        }
    }

    public void Run(string name, Func<string> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = action();
            sw.Stop();
            Record(name, true, detail, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Record(name, false, ex.Message, sw.Elapsed);
        }
    }

    private void Record(string name, bool passed, string detail, TimeSpan elapsed)
    {
        _results.Add((name, passed, detail, elapsed));
        var tag   = passed ? Green("PASS") : Red("FAIL");
        var time  = elapsed > TimeSpan.Zero ? $"  {elapsed.TotalMilliseconds:F0}ms" : "";
        var label = (name + "…").PadRight(60);
        Console.WriteLine($"  {label} {tag}{time}");
        if (!passed && !string.IsNullOrWhiteSpace(detail))
            Console.WriteLine($"         {Red("✗")} {detail}");
        else if (passed && !string.IsNullOrWhiteSpace(detail))
            Console.WriteLine($"         {Gray(detail)}");
    }

    // ── summary ───────────────────────────────────────────────────────────────

    public int PrintSummary()
    {
        _total.Stop();
        int passed = _results.Count(r => r.passed);
        int total  = _results.Count;
        int failed = total - passed;

        Console.WriteLine();
        Sep('─');
        if (failed == 0)
        {
            Console.WriteLine($"  {Green($"All {total} tests passed")}  ({_total.Elapsed.TotalSeconds:F1}s)");
        }
        else
        {
            Console.WriteLine($"  {Green($"{passed} passed")}  {Red($"{failed} failed")}  of {total}  ({_total.Elapsed.TotalSeconds:F1}s)");
            Console.WriteLine();
            Console.WriteLine("  Failed tests:");
            foreach (var (name, _, detail, _) in _results.Where(r => !r.passed))
                Console.WriteLine($"    {Red("✗")} {name}: {detail}");
        }
        Sep('─');
        return failed == 0 ? 0 : 1;
    }

    // ── section headers ───────────────────────────────────────────────────────

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"  {Cyan("▸")} {title}");
    }

    // ── console helpers ───────────────────────────────────────────────────────

    private static string Green(string s)  => $"\x1b[32m{s}\x1b[0m";
    private static string Red(string s)    => $"\x1b[31m{s}\x1b[0m";
    private static string Gray(string s)   => $"\x1b[90m{s}\x1b[0m";
    private static string Cyan(string s)   => $"\x1b[36m{s}\x1b[0m";

    public static void Sep(char c = '═', int w = 64) =>
        Console.WriteLine(new string(c, w));
}
