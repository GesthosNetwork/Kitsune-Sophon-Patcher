using System.Diagnostics;
using System.Text;

namespace SophonPatcher.Core;

public sealed record ArchiveInput(string ManifestPath, string ChunkDirectory, string? ArchivePath);

public static class Archive
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };

    public static string[] FindArchives(string applicationDirectory)
    {
        string root = Path.GetFullPath(applicationDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Application directory not found: {root}");

        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(GetArchivePriority)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void EnsureNoPendingInput(string applicationDirectory)
    {
        string root = Path.GetFullPath(applicationDirectory);
        string chunkDirectory = Path.Combine(root, "ldiff");
        string? manifestPath = FindManifestOrNull(root);

        if (Directory.Exists(chunkDirectory) || manifestPath is not null)
        {
            throw new InvalidOperationException("Unprocessed LDiff input already exists. Complete or remove the existing manifest and ldiff directory before extracting another archive.");
        }
    }

    public static async Task ExtractArchiveAsync(
        string applicationDirectory,
        string sevenZipPath,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(applicationDirectory);
        string fullArchivePath = Path.GetFullPath(archivePath);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullArchivePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive path is outside the application directory.");
        if (!File.Exists(fullArchivePath))
            throw new FileNotFoundException("Archive file was not found.", fullArchivePath);

        EnsureNoPendingInput(root);
        ProgressBar.WriteLine($"[INFO] Extracting archive: {Path.GetFileName(fullArchivePath)}");
        await ExtractHereAsync(sevenZipPath, fullArchivePath, root, cancellationToken);
        ProgressBar.WriteLine("[INFO] Archive extraction to disk completed.\n");
    }

    public static ArchiveInput? ResolveExtracted(string applicationDirectory, string archivePath)
    {
        string root = Path.GetFullPath(applicationDirectory);
        string chunkDirectory = Path.Combine(root, "ldiff");
        string? manifestPath = FindManifestOrNull(root);
        bool hasChunks = Directory.Exists(chunkDirectory);

        if (!hasChunks && manifestPath is null)
            return null;

        if (hasChunks != (manifestPath is not null))
        {
            throw new InvalidDataException($"Archive '{Path.GetFileName(archivePath)}' contains incomplete LDiff input. A manifest and ldiff directory must both be present.");
        }

        return new ArchiveInput(manifestPath!, chunkDirectory, Path.GetFullPath(archivePath));
    }

    public static ArchiveInput ResolveExisting(string applicationDirectory)
    {
        string root = Path.GetFullPath(applicationDirectory);
        string chunkDirectory = Path.Combine(root, "ldiff");
        string? manifestPath = FindManifestOrNull(root);
        bool hasChunks = Directory.Exists(chunkDirectory);

        if (hasChunks != (manifestPath is not null))
        {
            throw new InvalidDataException("Existing LDiff input is incomplete. A manifest and ldiff directory must both be present.");
        }

        if (!hasChunks)
        {
            throw new FileNotFoundException("No supported archive or extracted LDiff input was found beside sophonpatcher.exe.");
        }

        return new ArchiveInput(manifestPath!, chunkDirectory, null);
    }

    public static bool Cleanup(ArchiveInput input)
    {
        string root = Path.GetFullPath(Path.GetDirectoryName(input.ManifestPath) ?? AppContext.BaseDirectory);
        bool success = true;

        try
        {
            if (Directory.Exists(input.ChunkDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(input.ChunkDirectory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(input.ChunkDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            ProgressBar.WriteLine($"[WARNING] Could not remove LDiff directory: {ex.Message}");
            success = false;
        }

        string[] manifests = [];
        try
        {
            manifests = FindManifestFiles(root);
        }
        catch (Exception ex)
        {
            ProgressBar.WriteLine($"[WARNING] Could not enumerate manifest files: {ex.Message}");
            success = false;
        }

        foreach (string manifest in manifests)
        {
            try
            {
                File.SetAttributes(manifest, FileAttributes.Normal);
                File.Delete(manifest);
            }
            catch (Exception ex)
            {
                ProgressBar.WriteLine($"[WARNING] Could not remove manifest '{Path.GetFileName(manifest)}': {ex.Message}");
                success = false;
            }
        }

        if (success && !string.IsNullOrWhiteSpace(input.ArchivePath) && !DeleteArchive(input.ArchivePath))
            success = false;

        if (success)
            ProgressBar.WriteLine("[INFO] Patch input files cleaned up.\n");
        return success;
    }

    public static bool DeleteArchive(string archivePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(archivePath);
            if (File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, FileAttributes.Normal);
                File.Delete(fullPath);
            }
            ProgressBar.WriteLine($"[INFO] Removed archive: {Path.GetFileName(fullPath)}");
            return true;
        }
        catch (Exception ex)
        {
            ProgressBar.WriteLine($"[WARNING] Could not remove archive '{Path.GetFileName(archivePath)}': {ex.Message}");
            return false;
        }
    }

    private static int GetArchivePriority(string archivePath)
    {
        string name = Path.GetFileName(archivePath);
        if (name.StartsWith("game_", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.StartsWith("audio_", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.StartsWith("BeyondAssets_", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    private static string? FindManifestOrNull(string root)
    {
        string[] manifests = FindManifestFiles(root);
        string? exactManifest = manifests.FirstOrDefault(path =>
            Path.GetFileName(path).Equals("manifest", StringComparison.OrdinalIgnoreCase));
        if (exactManifest is not null)
            return exactManifest;

        if (manifests.Length == 1)
            return manifests[0];

        if (manifests.Length > 1)
        {
            string names = string.Join(", ", manifests.Select(Path.GetFileName));
            throw new InvalidOperationException($"Multiple manifest files were found ({names}). Keep only the manifest for the current archive.");
        }

        return null;
    }

    private static string[] FindManifestFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return name.Equals("manifest", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("manifest_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task ExtractHereAsync(
        string sevenZipPath,
        string archivePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add($"-o{outputDirectory}");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-aoa");
        startInfo.ArgumentList.Add("-bsp1");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The 7-Zip process could not be started.");

            ProgressBar.ReportPercent("ARCHIVE", 0, "Starting");
            Task<string> stdoutTask = ReadStandardOutputWithProgressAsync(
                process.StandardOutput, cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                ProgressBar.End();
                string details = string.Join(Environment.NewLine,
                    new[] { stderr.Trim(), stdout.Trim() }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                throw new InvalidOperationException(
                    $"7-Zip could not extract the archive (exit code {process.ExitCode})." +
                    (string.IsNullOrWhiteSpace(details) ? string.Empty : Environment.NewLine + details));
            }

            ProgressBar.ReportPercent("ARCHIVE", 100, "Complete");
            ProgressBar.End();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch {}
            throw;
        }
    }

    private static async Task<string> ReadStandardOutputWithProgressAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var digits = new StringBuilder(3);
        char[] buffer = new char[1024];

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            for (int index = 0; index < read; index++)
            {
                char current = buffer[index];
                output.Append(current);

                if (char.IsDigit(current))
                {
                    if (digits.Length < 3)
                        digits.Append(current);
                    else
                        digits.Clear();
                }
                else if (current == '%' && digits.Length > 0 &&
                         int.TryParse(digits.ToString(), out int percent) && percent is >= 0 and <= 100)
                {
                    ProgressBar.ReportPercent("ARCHIVE", percent);
                    digits.Clear();
                }
                else
                {
                    digits.Clear();
                }
            }
        }

        return output.ToString();
    }
}
