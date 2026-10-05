using System.Diagnostics;
using GameValueEditor.Modules.LastEpoch;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LiveTests;

public sealed class LastEpochLiveTest : IModuleLiveTest
{
    private const string ExpectedExecutableSha256 =
        "BD58F074BA47CF4BE285BA5D36427E0D2B94D00E59184D2981A5A1475979337E";
    private const string ExpectedGameAssemblySha256 =
        "66379C9E1B106C7A517AEF1419090ED2565D1C6689C7A6F644A196CE34B2C03D";
    private const string ExpectedMetadataSha256 =
        "CE1D015E1162C5529A935A89521F4F3733EB3D37F4CA2DD9688C53C0FC5EAF00";

    public string Game => "last-epoch";

    public async Task RunAsync(string[] args)
    {
        using var liveProcess = Process.GetProcessesByName("Last Epoch").SingleOrDefault()
                                ?? throw new InvalidOperationException("Last Epoch is not running.");
        var (process, build) = await LiveTestSupport.CreateContextAsync(liveProcess);
        LiveTestSupport.Assert(
            string.Equals(build.ExecutableSha256, ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(build.GameAssemblySha256, ExpectedGameAssemblySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(build.MetadataSha256, ExpectedMetadataSha256, StringComparison.OrdinalIgnoreCase),
            "The running Last Epoch build is not the exact build being approved for v0.5.3.");

        var adapter = new LastEpochGameAdapter();
        LiveTestSupport.Assert(adapter.Supports(process, build),
            "Last Epoch v0.5.3 rejected the current offline build during read-only semantic validation.");

        var diagnostics = ((IGameCompatibilityDiagnosticsProvider)adapter)
            .GetCompatibilityDiagnostics(process, build);
        LiveTestSupport.Assert(diagnostics.Any(item =>
                item.DisplayName == "离线进程" && item.Status == GameCompatibilityDiagnosticStatus.Passed) &&
            diagnostics.Any(item =>
                item.DisplayName == "IL2CPP 语义结构" && item.Status == GameCompatibilityDiagnosticStatus.Passed),
            "Last Epoch read-only diagnostics did not confirm offline mode and the complete IL2CPP structure.");
    }
}
