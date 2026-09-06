using System.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace RWire.Tests;

/// <summary>
/// Runs the entire R-side testthat suite (r/tests/testthat/*.R) as
/// part of `dotnet test`, so a change that breaks worker.R's
/// write_r_value/read_r_value/registry/frame-codec functions shows up
/// here instead of only being caught by a slower C# integration test
/// (or not at all, if nobody remembers to run
/// `Rscript tests/testthat.R` separately). This exercises worker.R's
/// logic directly via source() - no socket, no C# process - the same
/// way RValueCodecTests exercises the equivalent C# logic directly;
/// it does not replace the C# integration tests, which check the
/// actual wire round-trip between the two.
///
/// Requires Rscript on PATH, plus testthat and data.table installed
/// in that R environment (r/tests/testthat/test-value-codec.R skips
/// its data.table-specific test if the package isn't available, but
/// testthat itself must be present for anything here to run at all).
/// </summary>
public class RTestthatSuiteTests
{
    private static string RDirectory =>
        Path.Combine(AppContext.BaseDirectory, "r");

    [Fact]
    public async Task TestthatSuite_AllRSideUnitTestsPass()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "Rscript",
            WorkingDirectory = RDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("tests/testthat.R");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // testthat's own console output (pass/fail summary, and any
        // failure details) comes through stdout; R startup/package
        // warnings tend to land on stderr. Both are captured so a
        // failing assertion below can show the reader exactly what
        // testthat reported, rather than just "exit code 1".
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The R testthat suite did not complete within the 2-minute timeout.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        process.ExitCode.Should().Be(
            0,
            "the R testthat suite (r/tests/testthat) should pass - " +
            "see the captured output below for which test(s) failed:\n" +
            $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
    }
}
