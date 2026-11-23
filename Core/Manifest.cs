using ProtoBuf;
using SophonPatcher.Proto;
using ZstdSharp;

namespace SophonPatcher.Core;

public static class Manifest
{
    public static async Task<SophonPatchManifest> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (manifestBytes.Length == 0)
            throw new InvalidDataException("The LDiff manifest is empty.");

        SophonPatchManifest patchManifest;
        try
        {
            using MemoryStream direct = new(manifestBytes);
            patchManifest = Serializer.Deserialize<SophonPatchManifest>(direct);
            if (patchManifest.Patches.Count == 0)
                throw new InvalidDataException("Direct LDiff protobuf was empty.");
        }
        catch
        {
            using Decompressor decompressor = new();
            byte[] decompressedManifest = decompressor.Unwrap(manifestBytes).ToArray();
            using MemoryStream decompressed = new(decompressedManifest);
            patchManifest = Serializer.Deserialize<SophonPatchManifest>(decompressed);
        }

        return patchManifest;
    }

    public static List<SophonChunkFile> Build(
        SophonPatchManifest patchManifest,
        string sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(patchManifest);
        if (string.IsNullOrWhiteSpace(sourceVersion))
            throw new ArgumentException("Source version cannot be empty.", nameof(sourceVersion));

        var allFiles = new List<SophonChunkFile>();
        var artifactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonPatchFile patchFile in patchManifest.Patches)
        {
            SophonPatchInfo? patchInfo = patchFile.Patches.FirstOrDefault(x =>
                string.Equals(x.Tag, sourceVersion, StringComparison.OrdinalIgnoreCase));

            if (patchInfo?.Patch is null)
                continue;

            SophonPatch patch = patchInfo.Patch;
            if (string.IsNullOrWhiteSpace(patch.Id))
                throw new InvalidDataException($"Manifest entry '{patchFile.File}' has an empty chunk ID.");

            if (patch.PatchFileSize <= 0 || patch.PatchOffset < 0 || patch.PatchLength <= 0)
            {
                throw new InvalidDataException(
                    $"Invalid chunk range for '{patchFile.File}': " +
                    $"chunkSize={patch.PatchFileSize}, offset={patch.PatchOffset}, length={patch.PatchLength}.");
            }

            long rangeEnd;
            try { rangeEnd = checked(patch.PatchOffset + patch.PatchLength); }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"Chunk range overflows for '{patchFile.File}'.", ex);
            }

            if (rangeEnd > patch.PatchFileSize)
            {
                throw new InvalidDataException(
                    $"Chunk range exceeds the physical payload for '{patchFile.File}': " +
                    $"end={rangeEnd}, chunkSize={patch.PatchFileSize}.");
            }

            string targetFile = NormalizeRelativePath(patchFile.File, "target file");
            bool isNewFile = string.IsNullOrWhiteSpace(patch.OriginalFileName);
            string sourceFile = isNewFile
                ? string.Empty
                : NormalizeRelativePath(patch.OriginalFileName, "original file");
            string artifactFile = isNewFile ? targetFile : targetFile + ".hdiff";

            if (!artifactPaths.Add(artifactFile))
                throw new InvalidDataException($"Duplicate LDiff output path in manifest: '{artifactFile}'.");

            if (patchFile.Size < 0)
                throw new InvalidDataException($"Negative target size in manifest for '{targetFile}'.");

            var artifact = new SophonChunkFile
            {
                File = artifactFile,
                TargetFile = targetFile,
                SourceFile = sourceFile,
                IsNewFile = isNewFile,
                TargetSize = patchFile.Size,
                TargetMd5 = patchFile.Md5 ?? string.Empty,
                OriginalFileName = sourceFile,
                OriginalFileSize = patch.OriginalFileSize,
                OriginalFileMd5 = patch.OriginalFileMd5 ?? string.Empty
            };

            artifact.Chunks.Add(new SophonChunk
            {
                Id = patch.Id,
                CompressedSize = patch.PatchFileSize,
                CompressedMd5 = patch.PatchFileMd5 ?? string.Empty,
                Offset = patch.PatchOffset,
                Length = patch.PatchLength
            });

            allFiles.Add(artifact);
        }

        ValidatePhysicalChunkReferences(allFiles);
        ValidateNewFileSourceCollisions(allFiles);
        return allFiles;
    }

    private static void ValidatePhysicalChunkReferences(IEnumerable<SophonChunkFile> entries)
    {
        var physicalChunks = new Dictionary<string, (long Size, string Md5)>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonChunkFile entry in entries)
        {
            SophonChunk chunk = entry.Chunks.Single();
            if (!physicalChunks.TryGetValue(chunk.Id, out var existing))
            {
                physicalChunks.Add(chunk.Id, (chunk.CompressedSize, chunk.CompressedMd5));
                continue;
            }

            if (existing.Size != chunk.CompressedSize ||
                !existing.Md5.Equals(chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Manifest contains inconsistent physical chunk metadata for '{chunk.Id}'.");
            }
        }
    }

    private static void ValidateNewFileSourceCollisions(IEnumerable<SophonChunkFile> entries)
    {
        var originalSources = entries
            .Where(entry => !entry.IsNewFile)
            .Select(entry => entry.SourceFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? conflict = entries
            .Where(entry => entry.IsNewFile)
            .Select(entry => entry.TargetFile)
            .FirstOrDefault(originalSources.Contains);

        if (conflict is not null)
        {
            throw new InvalidDataException(
                $"A new-file target overlaps an original source path ('{conflict}'). " +
                "This manifest requires deferred new-file materialization to protect source data.");
        }
    }

    private static string NormalizeRelativePath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Manifest {description} path is empty.");

        string normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            throw new InvalidDataException($"Manifest {description} path must be relative: '{value}'.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"Manifest {description} path is unsafe: '{value}'.");

        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }
}
