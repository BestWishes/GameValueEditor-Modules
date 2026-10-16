using System.Reflection;
using System.Text;
using GameValueEditor.Modules.Deskrawl;

namespace GameValueEditor.Modules.LiveTests;

public sealed class DeskrawlMetadataFilesTest : IModuleLiveTest
{
    public string Game => "deskrawl-metadata-files";

    public Task RunAsync(string[] args)
    {
        var metadataType = typeof(DeskrawlGameAdapter).Assembly.GetType(
            "GameValueEditor.Modules.Deskrawl.DeskrawlApplicationMetadata", throwOnError: true)!;
        var root = Path.Combine(Path.GetTempPath(), $"DeskrawlMetadataTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var appInfo = Path.Combine(root, "app.info");
        var bootConfig = Path.Combine(root, "boot.config");
        var checks = 0;
        void Check(bool condition, string message)
        {
            LiveTestSupport.Assert(condition, message);
            checks++;
        }
        string Read(string method) => (string)metadataType.GetMethod(method,
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root])!;
        string Name() => Read("ReadProductName");
        string BuildGuid() => Read("ReadBuildGuid");

        try
        {
            Check(Name() == "" && BuildGuid() == "", "Missing startup files were not optional.");
            File.WriteAllText(appInfo, "First Day Games\r\nDeskrawl\r\n", new UTF8Encoding(true));
            File.WriteAllText(bootConfig, "build-guid=f2cada4bfd87488386b534169e539aac\r\n", new UTF8Encoding(true));
            Check(Name() == "Deskrawl", "The original product name/BOM was not read.");
            Check(BuildGuid() == "f2cada4bfd87488386b534169e539aac", "The original build GUID/BOM was not read.");

            foreach (var value in new[] { "", "not-a-guid", new string('0', 32),
                         "f2cada4b-fd87-4883-86b5-34169e539aac" })
            {
                File.WriteAllText(bootConfig, $"build-guid={value}\n");
                Check(BuildGuid() == "" && Name() == "Deskrawl", "An invalid GUID discarded the independent product name.");
            }
            File.WriteAllText(bootConfig,
                "build-guid=f2cada4bfd87488386b534169e539aac\nbuild-guid=f2cada4bfd87488386b534169e539aac\n");
            Check(BuildGuid() == "", "Duplicate GUID declarations were accepted.");
            File.WriteAllText(bootConfig, "build-guid=f2cada4bfd87488386b534169e539aac\n");

            File.WriteAllText(appInfo, "First Day Games\n");
            Check(Name() == "" && BuildGuid() != "", "An incomplete app.info discarded the independent GUID.");
            File.WriteAllText(appInfo, "First Day Games\n桌面破坏神\n");
            Check(Name() == "桌面破坏神", "A declared Unicode product name was replaced.");
            using (var locked = new FileStream(appInfo, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(Name() == "" && BuildGuid() != "", "An unreadable app.info discarded the independent GUID.");

            File.WriteAllBytes(appInfo, [0xFF, 0x00, 0xFF]);
            Check(Name() == "", "Invalid startup text was accepted.");
            File.WriteAllText(appInfo, new string('x', 16 * 1024 + 1));
            Check(Name() == "", "An oversized app.info was read.");
            File.WriteAllText(bootConfig, new string('x', 16 * 1024 + 1));
            Check(BuildGuid() == "", "An oversized boot.config was read.");

            Console.WriteLine($"Deskrawl metadata file regressions passed: {checks}. Synthetic files only; no game access.");
            return Task.CompletedTask;
        }
        finally
        {
            File.Delete(appInfo);
            File.Delete(bootConfig);
            Directory.Delete(root);
        }
    }
}
