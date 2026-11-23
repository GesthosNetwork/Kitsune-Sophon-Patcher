using System.Reflection;

namespace SophonPatcher.Core;

public sealed record EmbeddedTools(string SevenZipPath, string HpatchzPath);

public static class Embed
{
    private const string SevenZipResource = "SophonPatcher.Tools.7z.exe";
    private const string HpatchzResource = "SophonPatcher.Tools.hpatchz.exe";

    public static async Task<EmbeddedTools> EnsureToolsAsync(
        CancellationToken cancellationToken = default)
    {
        string toolsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SophonPatcher", "Tools");
        Directory.CreateDirectory(toolsDirectory);

        string sevenZipPath = await ExtractResourceAsync(
            SevenZipResource, "7z.exe", toolsDirectory, cancellationToken);
        string hpatchzPath = await ExtractResourceAsync(
            HpatchzResource, "hpatchz.exe", toolsDirectory, cancellationToken);

        return new EmbeddedTools(sevenZipPath, hpatchzPath);
    }

    private static async Task<string> ExtractResourceAsync(
        string resourceName,
        string fileName,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        await using Stream? resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            throw new FileNotFoundException(
                $"Embedded tool resource '{resourceName}' was not found. " +
                $"Make sure Tools\\{fileName} was present when sophonpatcher.exe was compiled.");
        }

        string destinationPath = Path.Combine(destinationDirectory, fileName);
        string temporaryPath = destinationPath + ".tmp";

        try
        {
            await using (var output = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await resource.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch {}
    }
}
