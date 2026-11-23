using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using SophonPatcher.Proto;

namespace SophonPatcher.Core;

public sealed record PatchSummary(int PatchedFiles, int SkippedFiles, int FailedFiles);

public static class Patch
{
    private const string TemporarySuffix = ".temp";
    private const int BufferSize = 1024 * 1024;

    public static async Task<PatchSummary> ApplyAvailableAsync(
        string workingDirectory,
        string hpatchzPath,
        IReadOnlyList<SophonChunkFile> extractedEntries,
        IReadOnlyList<SophonChunkFile> manifestEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extractedEntries);
        ArgumentNullException.ThrowIfNull(manifestEntries);

        string root = Path.GetFullPath(workingDirectory);
        SophonChunkFile[] patches = extractedEntries.Where(entry => !entry.IsNewFile).ToArray();
        SophonChunkFile[] allManifestPatches = manifestEntries.Where(entry => !entry.IsNewFile).ToArray();
        if (patches.Length == 0)
        {
            ProgressBar.WriteLine("[INFO] No extracted HDiff entries are available. Nothing to patch.");
            return new PatchSummary(0, 0, 0);
        }

        ProgressBar.WriteLine($"[INFO] Applying {patches.Length:N0} HDiff patch(es).");
        int patched = 0;
        int skipped = 0;
        int failed = 0;
        int processed = 0;
        var skipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var patchErrors = new List<string>();
        var sourceUseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceSuccessCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceHadFailure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonChunkFile entry in manifestEntries)
            targetPaths.Add(ResolveInsideRoot(root, entry.TargetFile));

        var extractedDiffPaths = extractedEntries
            .Where(entry => !entry.IsNewFile)
            .Select(entry => entry.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (SophonChunkFile patch in allManifestPatches)
        {
            string sourcePath = ResolveInsideRoot(root, patch.SourceFile);
            sourceUseCounts[sourcePath] = sourceUseCounts.GetValueOrDefault(sourcePath) + 1;
            if (!extractedDiffPaths.Contains(patch.File))
                sourceHadFailure.Add(sourcePath);
        }

        List<IGrouping<string, SophonChunkFile>> groups = patches
            .GroupBy(patch => ResolveInsideRoot(root, patch.SourceFile), StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<List<IGrouping<string, SophonChunkFile>>> batches = BuildPatchBatches(groups, root);

        ProgressBar.Report("PATCH", 0, patches.Length);

        foreach (List<IGrouping<string, SophonChunkFile>> batch in batches)
        {
            var staged = new List<PendingPatch>();

            try
            {
                foreach (IGrouping<string, SophonChunkFile> group in batch)
                {
                    string sourcePath = group.Key;
                    foreach (SophonChunkFile patch in group)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string diffPath = ResolveInsideRoot(root, patch.File);
                        string targetPath = ResolveInsideRoot(root, patch.TargetFile);

                        try
                        {
                            if (!File.Exists(diffPath))
                            {
                                skipReasons["Extracted HDiff segment missing"] = skipReasons.GetValueOrDefault("Extracted HDiff segment missing") + 1;
                                skipped++;
                                sourceHadFailure.Add(sourcePath);
                                continue;
                            }

                            if (!File.Exists(sourcePath))
                            {
                                skipReasons["Original source file not found"] = skipReasons.GetValueOrDefault("Original source file not found") + 1;
                                skipped++;
                                sourceHadFailure.Add(sourcePath);
                                continue;
                            }

                            string? sourceMismatch = await ValidateFileAsync(
                                sourcePath, patch.OriginalFileSize, patch.OriginalFileMd5, "original", cancellationToken);
                            if (sourceMismatch is not null)
                            {
                                skipReasons["Original file validation failed"] = skipReasons.GetValueOrDefault("Original file validation failed") + 1;
                                skipped++;
                                sourceHadFailure.Add(sourcePath);
                                continue;
                            }

                            string? targetDirectory = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(targetDirectory))
                                Directory.CreateDirectory(targetDirectory);

                            string temporaryOutputPath = targetPath + TemporarySuffix;
                            TryDeleteFile(temporaryOutputPath);

                            try
                            {
                                ProcessResult result = await RunHpatchzAsync(
                                    hpatchzPath, sourcePath, diffPath, temporaryOutputPath, cancellationToken);

                                if (result.ExitCode != 0 || !File.Exists(temporaryOutputPath))
                                {
                                    throw new InvalidOperationException(
                                        $"hpatchz exited with code {result.ExitCode}{DescribeExitCode(result.ExitCode)}.");
                                }

                                string? targetMismatch = await ValidateFileAsync(
                                    temporaryOutputPath, patch.TargetSize, patch.TargetMd5, "target", cancellationToken);
                                if (targetMismatch is not null)
                                    throw new InvalidDataException($"Patched output validation failed: {targetMismatch}");

                                staged.Add(new PendingPatch(sourcePath, targetPath, diffPath, temporaryOutputPath));
                            }
                            catch
                            {
                                TryDeleteFile(temporaryOutputPath);
                                throw;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            patchErrors.Add($"{patch.TargetFile}: {ex.Message}");
                            failed++;
                            sourceHadFailure.Add(sourcePath);
                        }
                    }
                }

                foreach (PendingPatch item in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (sourceHadFailure.Contains(item.TargetPath))
                        {
                            skipReasons["Kept source required by another unsuccessful patch"] =
                                skipReasons.GetValueOrDefault("Kept source required by another unsuccessful patch") + 1;
                            TryDeleteFile(item.TemporaryOutputPath);
                            sourceHadFailure.Add(item.SourcePath);
                            failed++;
                            continue;
                        }

                        string? targetDirectory = Path.GetDirectoryName(item.TargetPath);
                        if (!string.IsNullOrEmpty(targetDirectory))
                            Directory.CreateDirectory(targetDirectory);

                        File.Move(item.TemporaryOutputPath, item.TargetPath, overwrite: true);
                        sourceSuccessCounts[item.SourcePath] = sourceSuccessCounts.GetValueOrDefault(item.SourcePath) + 1;
                        patched++;

                        try { File.Delete(item.DiffPath); }
                        catch (Exception ex)
                        {
                            patchErrors.Add($"Could not remove HDiff segment '{Path.GetRelativePath(root, item.DiffPath)}': {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        TryDeleteFile(item.TemporaryOutputPath);
                        patchErrors.Add($"Could not commit '{Path.GetRelativePath(root, item.TargetPath)}': {ex.Message}");
                        failed++;
                        sourceHadFailure.Add(item.SourcePath);
                    }
                }

                processed += batch.Sum(group => group.Count());
                ProgressBar.Report("PATCH", processed, patches.Length);
            }
            catch (OperationCanceledException)
            {
                foreach (PendingPatch item in staged)
                    TryDeleteFile(item.TemporaryOutputPath);
                throw;
            }
            finally
            {
                foreach (PendingPatch item in staged)
                    TryDeleteFile(item.TemporaryOutputPath);
            }
        }

        foreach ((string sourcePath, int useCount) in sourceUseCounts)
        {
            if (sourceHadFailure.Contains(sourcePath) ||
                sourceSuccessCounts.GetValueOrDefault(sourcePath) != useCount ||
                targetPaths.Contains(sourcePath) ||
                !File.Exists(sourcePath)) { continue; }

            try { File.Delete(sourcePath); }
            catch (Exception ex)
            {
                patchErrors.Add($"Could not remove superseded original '{Path.GetRelativePath(root, sourcePath)}': {ex.Message}");
            }
        }

        ProgressBar.ReportPercent("PATCH", 100, $"{patches.Length:N0}/{patches.Length:N0}");
        ProgressBar.End();
        ProgressBar.WriteLine($"[SUMMARY] Patching finished. Successful={patched:N0}; Skipped={skipped:N0}; Failed={failed:N0}.");

        foreach ((string reason, int count) in skipReasons)
            ProgressBar.WriteLine($"[SUMMARY] Skipped - {reason}: {count:N0}");

        foreach (string error in patchErrors.Take(10))
            ProgressBar.WriteLine($"[ERROR] {error}");
        if (patchErrors.Count > 10)
            ProgressBar.WriteLine($"[WARNING] {patchErrors.Count - 10:N0} additional patch warning/error(s) omitted.");

        return new PatchSummary(patched, skipped, failed);
    }

    private static List<List<IGrouping<string, SophonChunkFile>>> BuildPatchBatches(
        List<IGrouping<string, SophonChunkFile>> groups,
        string root)
    {
        var sourceIndexes = groups
            .Select((group, index) => (SourcePath: group.Key, Index: index))
            .ToDictionary(item => item.SourcePath, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var dependencies = Enumerable.Range(0, groups.Count)
            .Select(_ => new HashSet<int>())
            .ToArray();
        var dependents = Enumerable.Range(0, groups.Count)
            .Select(_ => new List<int>())
            .ToArray();

        for (int index = 0; index < groups.Count; index++)
        {
            foreach (SophonChunkFile patch in groups[index])
            {
                string targetPath = ResolveInsideRoot(root, patch.TargetFile);
                if (sourceIndexes.TryGetValue(targetPath, out int dependency) && dependency != index)
                    dependencies[index].Add(dependency);
            }
        }

        int[] dependencyCounts = new int[groups.Count];
        for (int index = 0; index < groups.Count; index++)
        {
            dependencyCounts[index] = dependencies[index].Count;
            foreach (int dependency in dependencies[index])
                dependents[dependency].Add(index);
        }

        var ready = new Queue<int>(Enumerable.Range(0, groups.Count)
            .Where(index => dependencyCounts[index] == 0));
        var batches = new List<List<IGrouping<string, SophonChunkFile>>>();
        var processed = new bool[groups.Count];

        while (ready.Count > 0)
        {
            int index = ready.Dequeue();
            processed[index] = true;
            batches.Add([groups[index]]);

            foreach (int dependent in dependents[index])
            {
                dependencyCounts[dependent]--;
                if (dependencyCounts[dependent] == 0)
                    ready.Enqueue(dependent);
            }
        }

        List<IGrouping<string, SophonChunkFile>> cyclicBatch = Enumerable.Range(0, groups.Count)
            .Where(index => !processed[index])
            .Select(index => groups[index])
            .ToList();
        if (cyclicBatch.Count > 0)
            batches.Add(cyclicBatch);

        return batches;
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("A manifest file path is empty.");

        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest path escapes the application directory: '{relativePath}'.");
        }

        return fullPath;
    }

    private static async Task<string?> ValidateFileAsync(
        string path,
        long expectedSize,
        string expectedMd5,
        string description,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (expectedSize > 0 && info.Length != expectedSize)
        {
            return $"{description} size mismatch. Expected {FormatSize(expectedSize)}; actual {FormatSize(info.Length)}.";
        }

        if (!string.IsNullOrWhiteSpace(expectedMd5))
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string actualMd5 = await CalculateMd5Async(stream, cancellationToken);
            if (!actualMd5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                return $"{description} MD5 mismatch. Expected {expectedMd5}; actual {actualMd5}.";
            }
        }

        return null;
    }

    private static async Task<string> CalculateMd5Async(Stream stream, CancellationToken cancellationToken)
    {
        using var md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<ProcessResult> RunHpatchzAsync(
        string hpatchzPath,
        string sourcePath,
        string diffPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hpatchzPath,
            WorkingDirectory = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(diffPath);
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The hpatchz process could not be started.");

            Task<string> stdoutTask = ReadStandardOutputAsync(process.StandardOutput, cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);

            return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
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

    private static async Task<string> ReadStandardOutputAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string DescribeExitCode(int exitCode) => exitCode switch
    {
        1 => " - HPATCH_OPTIONS_ERROR",
        2 => " - HPATCH_OPENREAD_ERROR",
        3 => " - HPATCH_OPENWRITE_ERROR",
        4 => " - HPATCH_FILEREAD_ERROR",
        5 => " - HPATCH_FILEWRITE_ERROR",
        6 => " - HPATCH_FILEDATA_ERROR",
        7 => " - HPATCH_FILECLOSE_ERROR",
        8 => " - HPATCH_MEM_ERROR",
        9 => " - HPATCH_HDIFFINFO_ERROR",
        10 => " - HPATCH_COMPRESSTYPE_ERROR",
        11 => " - HPATCH_HPATCH_ERROR",
        12 => " - HPATCH_PATHTYPE_ERROR",
        13 => " - HPATCH_TEMPPATH_ERROR",
        14 => " - HPATCH_DELETEPATH_ERROR",
        15 => " - HPATCH_RENAMEPATH_ERROR",
        16 => " - HPATCH_SPATCH_ERROR",
        17 => " - HPATCH_BSPATCH_ERROR",
        18 => " - HPATCH_VCPATCH_ERROR",
        19 => " - HPATCH_WINPATCH_ERROR",
        20 => " - HPATCH_DECOMPRESSER_OPEN_ERROR",
        21 => " - HPATCH_DECOMPRESSER_CLOSE_ERROR",
        22 => " - HPATCH_DECOMPRESSER_MEM_ERROR",
        23 => " - HPATCH_DECOMPRESSER_DECOMPRESS_ERROR",
        24 => " - HPATCH_FILEWRITE_NO_SPACE_ERROR",
        25 => " - HPATCH_MULTITHREAD_ERROR",
        _ => string.Empty
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch {}
    }

    private sealed record PendingPatch(
        string SourcePath,
        string TargetPath,
        string DiffPath,
        string TemporaryOutputPath);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
