using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Modules.LiveTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var gameArgument = args.FirstOrDefault(argument =>
                argument.StartsWith("--game=", StringComparison.OrdinalIgnoreCase));
            var game = gameArgument?[(gameArgument.IndexOf('=') + 1)..]
                       ?? throw new InvalidOperationException("Pass --game=<game-directory>.");
            var tests = Assembly.GetExecutingAssembly().GetTypes()
                .Where(type => !type.IsAbstract && typeof(IModuleLiveTest).IsAssignableFrom(type))
                .Select(type => (IModuleLiveTest)Activator.CreateInstance(type)!)
                .ToList();
            var test = tests.SingleOrDefault(candidate =>
                           string.Equals(candidate.Game, game, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"No live test is registered for {game}.");
            await test.RunAsync(args);
            Console.WriteLine($"Live module test passed: {game}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

public interface IModuleLiveTest
{
    string Game { get; }
    Task RunAsync(string[] args);
}

public static class LiveTestSupport
{
    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task<(GameProcessContext Process, GameBuildIdentity Build)> CreateContextAsync(
        Process process)
    {
        var executablePath = process.MainModule?.FileName
                             ?? throw new InvalidOperationException("Unable to resolve the game executable path.");
        var root = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        var gameAssemblyPath = Path.Combine(root, "GameAssembly.dll");
        var metadataPath = Path.Combine(root, $"{executableName}_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var executableHash = await ComputeHashAsync(executablePath);
        var gameAssemblyHash = File.Exists(gameAssemblyPath) ? await ComputeHashAsync(gameAssemblyPath) : string.Empty;
        var metadataHash = File.Exists(metadataPath) ? await ComputeHashAsync(metadataPath) : string.Empty;
        var buildFingerprint = string.IsNullOrWhiteSpace(gameAssemblyHash) && string.IsNullOrWhiteSpace(metadataHash)
            ? executableHash
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"exe:{executableHash}\nassembly:{gameAssemblyHash}\nmetadata:{metadataHash}")));
        return (
            new GameProcessContext(process.Id, process.ProcessName, executablePath,
                process.StartTime.ToUniversalTime()),
            new GameBuildIdentity(executableHash, buildFingerprint, gameAssemblyHash, metadataHash));
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            1024 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
