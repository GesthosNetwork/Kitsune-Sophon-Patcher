using System.Reflection;
using System.Text;
using SophonPatcher.Core;
using SophonPatcher.Proto;

internal static class Program
{
    private static readonly Assembly ApplicationAssembly = typeof(Program).Assembly;
    private static readonly string Product =
        ApplicationAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? ApplicationAssembly.GetName().Name
        ?? "Unknown product";
    private static readonly string AssemblyTitle =
        ApplicationAssembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
        ?? Product;
    private static readonly string Copyright =
        ApplicationAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? string.Empty;

    private static readonly string AppVersion =
        ApplicationAssembly.GetName().Version?.ToString(3) ?? "Unknown";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = Encoding.UTF8;

        string applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        Directory.SetCurrentDirectory(applicationDirectory);

        SetTitle("Starting");

        Console.WriteLine(Product);
        Console.WriteLine(AssemblyTitle);
        Console.WriteLine(Copyright);
        Console.WriteLine(new string('-', 52));
        Console.WriteLine();

        RunMode mode;
        try
        {
            mode = ParseMode(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            PrintUsage();
            return 64;
        }

        if (mode == RunMode.Help)
        {
            PrintUsage();
            return 0;
        }

        int exitCode = 0;
        try
        {
            if (mode == RunMode.VerifyOnly)
            {
                Console.WriteLine("[INFO] Verify-only mode enabled.");
                bool verificationSucceeded = Verify.Run(cleanupUnlistedFiles: false, deleteUnlistedDirectoryOnSuccess: true);

                if (Verify.WasSkipped)
                {
                    Console.WriteLine("[WARNING] Verification was skipped because no pkg_version files were found.");
                    SetTitle("Verification skipped");
                }
                else if (!verificationSucceeded)
                {
                    if (Verify.CleanupFailed)
                    {
                        Console.WriteLine("[WARNING] File verification passed, but Unlisted_Files could not be deleted. See the log for details.");
                        SetTitle("Cleanup failed");
                    }
                    else
                    {
                        Console.WriteLine("[WARNING] File verification failed. See the verification report for details.");
                        SetTitle("Verification failed");
                    }
                    exitCode = 2;
                }
                else if (Verify.HasUnlistedFiles)
                {
                    Console.WriteLine("[WARNING] File verification passed, but other unlisted files were found. They were left untouched.");
                    SetTitle("Completed with unlisted files");
                }
                else
                {
                    Console.WriteLine("[SUCCESS] File verification passed.");
                    SetTitle("Verification passed");
                }
            }
            else
            {
                bool extractOnly = mode == RunMode.ExtractOnly;
                EmbeddedTools tools = await Embed.EnsureToolsAsync();

                int archivesProcessed = 0;
                int ldiffProcesses = 0;
                int totalPatchEntries = 0;
                int totalPhysicalChunks = 0;
                int totalExtractedEntries = 0;
                int totalExtractionFailures = 0;
                int totalSkippedExtractionFiles = 0;
                int totalPatchedFiles = 0;
                int totalSkippedPatches = 0;
                int totalFailedPatches = 0;

                if (extractOnly)
                    Console.WriteLine("[INFO] Extract-only mode enabled. HDiff patching and final verification will be skipped.");

                string[] archives = Archive.FindArchives(applicationDirectory);
                if (archives.Length > 0)
                {
                    for (int index = 0; index < archives.Length; index++)
                    {
                        string archivePath = archives[index];
                        string archiveName = Path.GetFileName(archivePath);
                        Console.WriteLine($"[INFO] Archive {index + 1:N0}/{archives.Length:N0}: {archiveName}");

                        SetTitle($"Extracting {archiveName}");
                        await Archive.ExtractArchiveAsync(applicationDirectory, tools.SevenZipPath, archivePath);

                        ArchiveInput? input = Archive.ResolveExtracted(applicationDirectory, archivePath);
                        if (input is null)
                        {
                            if (!Archive.DeleteArchive(archivePath))
                                throw new IOException($"Could not remove processed archive '{archiveName}'.");

                            archivesProcessed++;
                            Console.WriteLine($"[INFO] No LDiff manifest found. Archive payload extracted directly: {archiveName}");
                            continue;
                        }

                        InputSummary summary = await ProcessLdiffInputAsync(input, applicationDirectory, tools.HpatchzPath, extractOnly);

                        EnsureProcessSucceeded(summary, archiveName, extractOnly);

                        if (!Archive.Cleanup(input))
                        {
                            throw new IOException($"Could not clean up input files for '{archiveName}'. The next archive was not started.");
                        }

                        Accumulate(summary);
                        archivesProcessed++;
                        ldiffProcesses++;
                    }
                }
                else
                {
                    ArchiveInput input = Archive.ResolveExisting(applicationDirectory);
                    InputSummary summary = await ProcessLdiffInputAsync(input, applicationDirectory, tools.HpatchzPath, extractOnly);

                    EnsureProcessSucceeded(summary, "existing LDiff input", extractOnly);

                    if (!Archive.Cleanup(input))
                        throw new IOException("Could not clean up the processed LDiff input.");

                    Accumulate(summary);
                    ldiffProcesses++;
                }

                bool verificationSucceeded = true;
                if (extractOnly)
                {
                    Console.WriteLine("[INFO] Extract-only run complete. Extracted files and patch segments were retained.");
                }
                else
                {
                    Console.WriteLine();
                    SetTitle("Verifying game files...");
                    Console.WriteLine("[INFO] Verifying file integrity...");
                    verificationSucceeded = Verify.Run();
                }

                bool hasWarnings = totalExtractionFailures > 0 ||
                    totalSkippedPatches > 0 ||
                    totalFailedPatches > 0 ||
                    (!extractOnly && !verificationSucceeded);

                Console.WriteLine(extractOnly
                    ? "[SUCCESS] Extract Only completed. Patching and verification were not performed."
                    : hasWarnings
                        ? "[WARNING] SophonPatcher finished with verification warnings."
                        : "[SUCCESS] SophonPatcher has finished.");
                Console.WriteLine($"[SUMMARY] Archives processed: {archivesProcessed:N0}");
                Console.WriteLine($"[SUMMARY] LDiff processes: {ldiffProcesses:N0}");
                Console.WriteLine($"[SUMMARY] Manifest entries: {totalPatchEntries:N0}");
                Console.WriteLine($"[SUMMARY] Referenced physical chunks: {totalPhysicalChunks:N0}");
                Console.WriteLine($"[SUMMARY] Extracted entries: {totalExtractedEntries:N0}");
                Console.WriteLine($"[SUMMARY] Extraction failures: {totalExtractionFailures:N0}");
                Console.WriteLine($"[SUMMARY] Skipped incomplete files: {totalSkippedExtractionFiles:N0}");
                Console.WriteLine($"[SUMMARY] Patched files: {totalPatchedFiles:N0}");
                Console.WriteLine($"[SUMMARY] Skipped patches: {totalSkippedPatches:N0}");
                Console.WriteLine($"[SUMMARY] Failed patches: {totalFailedPatches:N0}");
                Console.WriteLine($"[SUMMARY] Verification: {(extractOnly ? "SKIPPED (extract-only)" : Verify.WasSkipped ? "SKIPPED" : verificationSucceeded ? "PASSED" : "FAILED")}");

                if (extractOnly)
                {
                    SetTitle("Extraction completed");
                }
                else if (hasWarnings)
                {
                    exitCode = 2;
                    SetTitle("Completed with warnings");
                }
                else if (Verify.WasSkipped)
                {
                    SetTitle("Completed - verification skipped");
                }
                else if (Verify.HasUnlistedFiles)
                {
                    SetTitle("Completed with unlisted files");
                }
                else
                {
                    SetTitle("Completed successfully");
                }

                void Accumulate(InputSummary summary)
                {
                    totalPatchEntries += summary.PatchEntries;
                    totalPhysicalChunks += summary.PhysicalChunks;
                    totalExtractedEntries += summary.Extraction.ExtractedEntries.Count;
                    totalExtractionFailures += summary.Extraction.FailedFiles;
                    totalSkippedExtractionFiles += summary.Extraction.SkippedFiles;
                    totalPatchedFiles += summary.Patching.PatchedFiles;
                    totalSkippedPatches += summary.Patching.SkippedFiles;
                    totalFailedPatches += summary.Patching.FailedFiles;
                }
            }
        }
        catch (Exception ex)
        {
            ProgressBar.End();
            Console.WriteLine();
            Console.WriteLine($"[FATAL] {ex.Message}");
            SetTitle("Failed");
            exitCode = 1;
        }

        Console.WriteLine();
        Console.WriteLine("Press any key to close this window...");
        try { Console.ReadKey(intercept: true); }
        catch {}

        return exitCode;
    }

    private static RunMode ParseMode(string[] args)
    {
        if (args.Length == 0)
            return RunMode.Full;

        if (args.Length > 1)
            throw new ArgumentException("Only one command-line option can be used at a time.");

        return args[0].ToLowerInvariant() switch
        {
            "--extract" or "-e" => RunMode.ExtractOnly,
            "--verify" or "-v" => RunMode.VerifyOnly,
            "--help" or "-h" => RunMode.Help,
            _ => throw new ArgumentException($"Unknown option '{args[0]}'.")
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SophonPatcher.exe             Extract and patch (default)");
        Console.WriteLine("  SophonPatcher.exe --extract   Extract only");
        Console.WriteLine("  SophonPatcher.exe -e          Alias for --extract");
        Console.WriteLine("  SophonPatcher.exe --verify    Verify only");
        Console.WriteLine("  SophonPatcher.exe -v          Alias for --verify");
        Console.WriteLine("  SophonPatcher.exe --help      Show this help");
    }

    private static async Task<InputSummary> ProcessLdiffInputAsync(
        ArchiveInput input,
        string applicationDirectory,
        string hpatchzPath,
        bool extractOnly)
    {
        SetTitle("Reading manifest...");
        Console.WriteLine("[INFO] Reading the LDiff manifest...");
        Console.WriteLine($"[INFO] Manifest: {Path.GetFileName(input.ManifestPath)}");
        ManifestDocument manifestDocument = await Manifest.ReadAsync(input.ManifestPath);
        bool isChunkManifest = manifestDocument.Format == SophonManifestFormat.Chunk;

        List<SophonChunkFile> artifacts;
        if (isChunkManifest)
        {
            Console.WriteLine("[INFO] Manifest format: Sophon Chunk.");
            artifacts = Manifest.Build(manifestDocument.ChunkManifest!);
        }
        else
        {
            Console.WriteLine("[INFO] Manifest format: Sophon Patch.");
            SophonPatchManifest patchManifest = manifestDocument.PatchManifest!;
            string sourceVersion = SelectSourceVersion(patchManifest, input.ChunkDirectory);
            Console.WriteLine($"[INFO] Source version: {sourceVersion}");
            artifacts = Manifest.Build(patchManifest, sourceVersion);
        }

        if (artifacts.Count == 0)
            throw new InvalidDataException("No extractable entries were found in the LDiff manifest.");

        int physicalChunkCount = artifacts
            .SelectMany(entry => entry.Chunks)
            .Select(chunk => chunk.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var chunkStore = new ChunkStore(applicationDirectory);
        int availableChunkCount = artifacts
            .SelectMany(entry => entry.Chunks)
            .Select(chunk => chunk.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(chunkId => File.Exists(chunkStore.GetChunkPath(chunkId)));
        int newFileCount = artifacts.Count(entry => entry.IsNewFile && !entry.IsFolder);
        int hdiffCount = artifacts.Count(entry => !entry.IsNewFile);
        int completeChunkFiles = isChunkManifest
            ? artifacts.Count(entry => !entry.IsFolder && entry.Chunks.All(chunk => File.Exists(chunkStore.GetChunkPath(chunk.Id))))
            : 0;

        Console.WriteLine($"[INFO] Manifest entries: {artifacts.Count:N0}");
        Console.WriteLine(isChunkManifest
            ? $"[INFO] Physical chunk files available: {availableChunkCount:N0}/{physicalChunkCount:N0}"
            : $"[INFO] Physical chunk files: {physicalChunkCount:N0}");
        if (isChunkManifest)
            Console.WriteLine($"[INFO] Files with all chunks available: {completeChunkFiles:N0}/{newFileCount:N0}");
        else
        {
            Console.WriteLine($"[INFO] HDiff entries: {hdiffCount:N0}");
            Console.WriteLine($"[INFO] New-file entries: {newFileCount:N0}");
        }
        Console.WriteLine();

        SetTitle("Extracting LDiff...");
        Console.WriteLine(isChunkManifest
            ? "[INFO] Reconstructing files from Zstandard chunks..."
            : "[INFO] Extracting LDiff segments and new files...");

        var extractor = new Extract();
        ExtractSummary extractionSummary = isChunkManifest
            ? await extractor.ExtractChunkManifestAsync(artifacts, applicationDirectory)
            : await extractor.ExtractAsync(artifacts, applicationDirectory);

        PatchSummary patchSummary = new(0, 0, 0);
        if (isChunkManifest)
        {
            Console.WriteLine("[INFO] Sophon Chunk files are complete files; HDiff patch application is not required.");
        }
        else if (extractOnly)
        {
            Console.WriteLine("[INFO] Extract-only mode: HDiff patch application was skipped.");
        }
        else
        {
            SetTitle("Applying HDiff patches...");
            Console.WriteLine("[INFO] Starting HDiff patch application...");
            patchSummary = await Patch.ApplyAvailableAsync(applicationDirectory, hpatchzPath, extractionSummary.ExtractedEntries, artifacts);
        }

        return new InputSummary(artifacts.Count, physicalChunkCount, extractionSummary, patchSummary);
    }

    private static void EnsureProcessSucceeded(InputSummary summary, string processName, bool extractOnly)
    {
        if (summary.Extraction.FailedFiles > 0 ||
            (summary.Extraction.SkippedFiles > 0 && summary.Extraction.ExtractedEntries.Count == 0) ||
            (!extractOnly && (summary.Patching.SkippedFiles > 0 || summary.Patching.FailedFiles > 0)))
        {
            string operation = extractOnly ? "extraction" : "extraction or patching";
            throw new InvalidOperationException($"The {operation} process for '{processName}' was not completely successful. Its manifest, ldiff directory, and archive have been retained. Remaining archives were not processed and final verification was not started.");
        }
    }

    private enum RunMode
    {
        Full, ExtractOnly, VerifyOnly, Help
    }

    internal static void SetTitle(string status)
    {
        try
        {
            Console.Title = $"Kitsune Sophon Patcher v{AppVersion} - {status}";
        }
        catch {}
    }

    private static string SelectSourceVersion(
        SophonPatchManifest manifest,
        string chunkDirectory)
    {
        string[] versions = manifest.Patches
            .SelectMany(file => file.Patches)
            .Where(patchInfo => patchInfo.Patch is not null)
            .Select(patchInfo => patchInfo.Tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (versions.Length == 0)
            throw new InvalidDataException("The LDiff manifest does not contain any source-version tags with patch data.");
        if (versions.Length == 1)
            return versions[0];
        if (!Directory.Exists(chunkDirectory))
            throw new DirectoryNotFoundException($"LDiff cache not found: {chunkDirectory}");

        string applicationDirectory = Path.GetDirectoryName(Path.GetFullPath(chunkDirectory))
            ?? AppContext.BaseDirectory;
        var chunkStore = new ChunkStore(applicationDirectory);
        var candidates = new List<SourceVersionCandidate>();

        foreach (string version in versions)
        {
            List<SophonChunkFile> artifacts;
            try
            {
                artifacts = Manifest.Build(manifest, version);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                continue;
            }

            SophonChunk[] chunks = artifacts
                .SelectMany(artifact => artifact.Chunks)
                .GroupBy(chunk => chunk.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            if (chunks.Length == 0)
                continue;

            var chunkIds = chunks
                .Select(chunk => chunk.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int availableChunks = chunks.Count(chunk =>
                File.Exists(chunkStore.GetChunkPath(chunk.Id)));
            int validChunks = chunks.Count(chunk =>
                chunkStore.HasChunk(chunk.Id, chunk.CompressedSize));

            candidates.Add(new SourceVersionCandidate(version, chunkIds, availableChunks, validChunks));
        }

        if (candidates.Count == 0)
            throw new InvalidDataException("No valid source-version patch entries were found in the LDiff manifest.");

        HashSet<string> allChunkIds = candidates
            .SelectMany(candidate => candidate.ChunkIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cachedChunkIds = Directory
            .EnumerateFiles(chunkDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(fileName => fileName is not null && allChunkIds.Contains(fileName))
            .Select(fileName => fileName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        SourceVersionCandidate[] matchingCandidates = candidates
            .Where(candidate => cachedChunkIds.Count > 0 && cachedChunkIds.All(candidate.ChunkIds.Contains))
            .ToArray();
        SourceVersionCandidate[] completeCandidates = candidates
            .Where(candidate => candidate.ValidChunks == candidate.ChunkIds.Count)
            .ToArray();

        SourceVersionCandidate? selected = null;
        SourceVersionCandidate[] selectable = [];

        if (matchingCandidates.Length == 1)
        {
            selected = matchingCandidates[0];
        }
        else if (matchingCandidates.Length > 1)
        {
            SourceVersionCandidate[] completeMatches = matchingCandidates
                .Where(candidate => candidate.ValidChunks == candidate.ChunkIds.Count)
                .ToArray();

            if (completeMatches.Length == 1)
                selected = completeMatches[0];
            else
                selectable = completeMatches.Length > 1 ? completeMatches : matchingCandidates;
        }
        else if (completeCandidates.Length == 1)
        {
            selected = completeCandidates[0];
        }
        else if (completeCandidates.Length > 1)
        {
            selectable = completeCandidates;
        }

        if (selected is not null)
        {
            Console.WriteLine(
                $"[INFO] Source version detected from LDiff cache: {selected.Version} " +
                $"({cachedChunkIds.Count:N0} matching chunk files).");
            return selected.Version;
        }

        if (selectable.Length == 0)
        {
            string candidateDetails = string.Join(", ", candidates.Select(candidate =>
                $"{candidate.Version} ({candidate.ValidChunks:N0}/{candidate.ChunkIds.Count:N0} chunks size-verified)"));
            throw new InvalidDataException(
                "Could not identify a source version from the LDiff cache. " +
                $"Recognized chunk files: {cachedChunkIds.Count:N0}. Candidates: {candidateDetails}. " +
                "Check that the manifest and ldiff directory belong to the same update and that all chunk files are present.");
        }

        Console.WriteLine("[INPUT] The available LDiff chunks match multiple source versions:");
        for (int index = 0; index < selectable.Length; index++)
        {
            SourceVersionCandidate candidate = selectable[index];
            Console.WriteLine(
                $"  [{index + 1}] {candidate.Version} " +
                $"({candidate.AvailableChunks:N0}/{candidate.ChunkIds.Count:N0} chunks found)");
        }

        while (true)
        {
            Console.Write("Enter selection: ");
            string? value = Console.ReadLine();
            if (int.TryParse(value, out int selection) && selection >= 1 && selection <= selectable.Length)
                return selectable[selection - 1].Version;
            Console.WriteLine("[WARNING] Invalid selection. Try again.");
        }
    }

    private sealed record SourceVersionCandidate(
        string Version,
        HashSet<string> ChunkIds,
        int AvailableChunks,
        int ValidChunks);

    private sealed record InputSummary(
        int PatchEntries,
        int PhysicalChunks,
        ExtractSummary Extraction,
        PatchSummary Patching);
}
