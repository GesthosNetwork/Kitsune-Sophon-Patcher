using ProtoBuf;
using SophonPatcher.Proto;
using ZstdSharp;

namespace SophonPatcher.Core;

public enum SophonManifestFormat
{
    Patch,
    Chunk
}

public sealed record ManifestDocument(
    SophonManifestFormat Format,
    SophonPatchManifest? PatchManifest,
    SophonChunkManifest? ChunkManifest);

public static class Manifest
{
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    public static async Task<ManifestDocument> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (manifestBytes.Length == 0)
            throw new InvalidDataException("The LDiff manifest is empty.");

        bool isZstd = manifestBytes.AsSpan().StartsWith(ZstdMagic);
        byte[] protobufBytes = manifestBytes;
        Exception? decompressionError = null;

        if (isZstd)
        {
            try
            {
                using Decompressor decompressor = new();
                protobufBytes = decompressor.Unwrap(manifestBytes).ToArray();
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The LDiff manifest has a Zstandard header but could not be decompressed.", ex);
            }
        }

        if (TryDeserializeDocument(protobufBytes, out ManifestDocument? document, out string parseErrors))
            return document!;

        if (!isZstd)
        {
            try
            {
                using Decompressor decompressor = new();
                byte[] decompressed = decompressor.Unwrap(manifestBytes).ToArray();
                if (TryDeserializeDocument(decompressed, out document, out string decompressedErrors))
                    return document!;
                parseErrors += $" Decompressed parse: {decompressedErrors}";
            }
            catch (Exception ex)
            {
                decompressionError = ex;
            }
        }

        string message = "The LDiff manifest is not a supported Sophon Chunk or Patch manifest." +
            (string.IsNullOrWhiteSpace(parseErrors) ? string.Empty : $" {parseErrors}");
        throw decompressionError is null
            ? new InvalidDataException(message)
            : new InvalidDataException(message, decompressionError);
    }

    private static bool TryDeserializeDocument(
        byte[] protobufBytes,
        out ManifestDocument? document,
        out string errors)
    {
        var failures = new List<string>(2);

        try
        {
            using MemoryStream stream = new(protobufBytes, writable: false);
            SophonChunkManifest chunkManifest = Serializer.Deserialize<SophonChunkManifest>(stream);
            if (IsValidChunkManifest(chunkManifest))
            {
                document = new ManifestDocument(SophonManifestFormat.Chunk, null, chunkManifest);
                errors = string.Empty;
                return true;
            }
            failures.Add("Chunk manifest validation found no file entries.");
        }
        catch (Exception ex)
        {
            failures.Add($"Chunk parser: {ex.Message}");
        }

        try
        {
            using MemoryStream stream = new(protobufBytes, writable: false);
            SophonPatchManifest patchManifest = Serializer.Deserialize<SophonPatchManifest>(stream);
            if (IsValidPatchManifest(patchManifest))
            {
                document = new ManifestDocument(SophonManifestFormat.Patch, patchManifest, null);
                errors = string.Empty;
                return true;
            }
            failures.Add("Patch manifest validation found no patch entries.");
        }
        catch (Exception ex)
        {
            failures.Add($"Patch parser: {ex.Message}");
        }

        document = null;
        errors = string.Join(" ", failures);
        return false;
    }

    private static bool IsValidChunkManifest(SophonChunkManifest manifest)
    {
        return manifest.Files.Count > 0 &&
            manifest.Files.Any(file => !string.IsNullOrWhiteSpace(file.File)) &&
            manifest.Files.All(file => file.Chunks.All(chunk => !string.IsNullOrWhiteSpace(chunk.Id)));
    }

    private static bool IsValidPatchManifest(SophonPatchManifest manifest)
    {
        return manifest.Patches.Count > 0 &&
            manifest.Patches.Any(file => !string.IsNullOrWhiteSpace(file.File));
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

    public static List<SophonChunkFile> Build(SophonChunkManifest chunkManifest)
    {
        ArgumentNullException.ThrowIfNull(chunkManifest);
        if (chunkManifest.Files.Count == 0)
            throw new InvalidDataException("The Sophon Chunk manifest contains no files.");

        var allFiles = new List<SophonChunkFile>(chunkManifest.Files.Count);
        var artifactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var physicalChunks = new Dictionary<string, ChunkMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (SophonChunkManifestFile manifestFile in chunkManifest.Files)
        {
            string targetFile = NormalizeRelativePath(manifestFile.File, "target file");
            if (!artifactPaths.Add(targetFile))
                throw new InvalidDataException($"Duplicate target path in Sophon Chunk manifest: '{targetFile}'.");
            if (manifestFile.Size < 0)
                throw new InvalidDataException($"Negative target size in manifest for '{targetFile}'.");
            if (manifestFile.IsFolder && manifestFile.Chunks.Count > 0)
                throw new InvalidDataException($"Folder entry '{targetFile}' unexpectedly contains file chunks.");

            var artifact = new SophonChunkFile
            {
                File = targetFile,
                TargetFile = targetFile,
                IsNewFile = true,
                IsFolder = manifestFile.IsFolder,
                IsChunkManifestFile = true,
                TargetSize = manifestFile.Size,
                TargetMd5 = manifestFile.Md5 ?? string.Empty
            };

            foreach (SophonManifestChunk manifestChunk in manifestFile.Chunks)
            {
                ValidateChunkId(manifestChunk.Id, targetFile);

                if (manifestChunk.Offset < 0 ||
                    manifestChunk.CompressedSize < 0 ||
                    manifestChunk.UncompressedSize < 0 ||
                    (manifestChunk.UncompressedSize > 0 && manifestChunk.CompressedSize == 0))
                {
                    throw new InvalidDataException(
                        $"Invalid chunk metadata for '{targetFile}' ({manifestChunk.Id}): " +
                        $"offset={manifestChunk.Offset}, compressedSize={manifestChunk.CompressedSize}, " +
                        $"uncompressedSize={manifestChunk.UncompressedSize}.");
                }

                long rangeEnd;
                try { rangeEnd = checked(manifestChunk.Offset + manifestChunk.UncompressedSize); }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException($"Chunk range overflows for '{targetFile}' ({manifestChunk.Id}).", ex);
                }

                if (rangeEnd > manifestFile.Size)
                {
                    throw new InvalidDataException(
                        $"Chunk range exceeds target file '{targetFile}': " +
                        $"end={rangeEnd}, fileSize={manifestFile.Size}.");
                }

                artifact.Chunks.Add(new SophonChunk
                {
                    Id = manifestChunk.Id,
                    CompressedSize = manifestChunk.CompressedSize,
                    CompressedMd5 = manifestChunk.CompressedMd5 ?? string.Empty,
                    UncompressedMd5 = manifestChunk.UncompressedMd5 ?? string.Empty,
                    Offset = manifestChunk.Offset,
                    Length = manifestChunk.UncompressedSize,
                    UncompressedSize = manifestChunk.UncompressedSize,
                    Unknown = manifestChunk.Unknown
                });

                var currentMetadata = new ChunkMetadata(
                    manifestChunk.CompressedSize,
                    manifestChunk.CompressedMd5 ?? string.Empty,
                    manifestChunk.UncompressedSize,
                    manifestChunk.UncompressedMd5 ?? string.Empty);

                if (!physicalChunks.TryGetValue(manifestChunk.Id, out ChunkMetadata? existing))
                {
                    physicalChunks.Add(manifestChunk.Id, currentMetadata);
                }
                else
                {
                    if (existing.CompressedSize != currentMetadata.CompressedSize ||
                        !existing.CompressedMd5.Equals(currentMetadata.CompressedMd5, StringComparison.OrdinalIgnoreCase) ||
                        existing.UncompressedSize != currentMetadata.UncompressedSize ||
                        !existing.UncompressedMd5.Equals(currentMetadata.UncompressedMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Manifest contains inconsistent physical chunk metadata for '{manifestChunk.Id}'.");
                    }
                }
            }

            ValidateChunkCoverage(artifact);
            allFiles.Add(artifact);
        }

        return allFiles;
    }

    private static void ValidateChunkCoverage(SophonChunkFile entry)
    {
        if (entry.IsFolder)
            return;

        if (entry.Chunks.Count == 0)
        {
            if (entry.TargetSize != 0)
                throw new InvalidDataException($"File '{entry.TargetFile}' has a non-zero size but no chunk references.");
            return;
        }

        long expectedOffset = 0;
        foreach (SophonChunk chunk in entry.Chunks.OrderBy(chunk => chunk.Offset))
        {
            if (chunk.Offset != expectedOffset)
            {
                throw new InvalidDataException(
                    $"Chunk ranges for '{entry.TargetFile}' contain a gap or overlap at offset {expectedOffset:N0}.");
            }
            expectedOffset = checked(chunk.Offset + chunk.UncompressedSize);
        }

        if (expectedOffset != entry.TargetSize)
        {
            throw new InvalidDataException(
                $"Chunk ranges for '{entry.TargetFile}' cover {expectedOffset:N0} bytes, " +
                $"but the target file size is {entry.TargetSize:N0} bytes.");
        }
    }

    private static void ValidateChunkId(string chunkId, string targetFile)
    {
        if (string.IsNullOrWhiteSpace(chunkId) ||
            chunkId.Contains('/') ||
            chunkId.Contains('\\') ||
            chunkId is "." or ".." ||
            chunkId.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid physical chunk ID for '{targetFile}': '{chunkId}'.");
        }
    }

    private sealed record ChunkMetadata(
        long CompressedSize,
        string CompressedMd5,
        long UncompressedSize,
        string UncompressedMd5);

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
