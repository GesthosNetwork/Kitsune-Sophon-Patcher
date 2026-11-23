using System.Security.Cryptography;
using System.Text;
using SophonPatcher.Proto;
using ZstdSharp;

namespace SophonPatcher.Core;

public sealed record ExtractSummary(
    IReadOnlyList<SophonChunkFile> ExtractedEntries,
    int FailedFiles);

public sealed class Extract
{
    private const int BufferSize = 1024 * 1024;
    private const int HeaderProbeSize = 128;
    private static readonly byte[] HdiffSignature = Encoding.ASCII.GetBytes("HDIFF13&zstd");
    private static readonly byte[] ZstdSignature = [0x28, 0xB5, 0x2F, 0xFD];

    public async Task<ExtractSummary> ExtractAsync(
        List<SophonChunkFile> allFiles,
        string saveDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allFiles);
        if (string.IsNullOrWhiteSpace(saveDirectory))
            throw new ArgumentException("Save directory cannot be empty.", nameof(saveDirectory));

        string root = Path.GetFullPath(saveDirectory);
        var chunkStore = new ChunkStore(root);
        if (!Directory.Exists(chunkStore.ChunkDirectory))
            throw new DirectoryNotFoundException($"Chunk cache not found: {chunkStore.ChunkDirectory}");

        int uniqueChunkCount = allFiles
            .SelectMany(file => file.Chunks)
            .Select(chunk => chunk.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int newFileCount = allFiles.Count(file => file.IsNewFile);
        int hdiffCount = allFiles.Count - newFileCount;

        ProgressBar.WriteLine(
            $"[INFO] Starting LDiff extraction. Entries={allFiles.Count:N0}; " +
            $"PhysicalChunks={uniqueChunkCount:N0}; HDiff={hdiffCount:N0}; NewFiles={newFileCount:N0}.");

        var validatedChunks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidChunks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extracted = new List<SophonChunkFile>(allFiles.Count);
        var errors = new List<string>();
        int failed = 0;

        ProgressBar.Report("EXTRACT", 0, allFiles.Count);
        try
        {
            for (int index = 0; index < allFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SophonChunkFile entry = allFiles[index];
                string entryKind = entry.IsNewFile ? "NEW" : "HDIFF";
                bool itemFinished = false;

                try
                {
                    if (entry.Chunks.Count != 1)
                        throw new InvalidDataException($"Expected exactly one physical chunk reference for '{entry.File}'.");

                    SophonChunk chunk = entry.Chunks[0];
                    if (invalidChunks.TryGetValue(chunk.Id, out string? priorError))
                        throw new InvalidDataException($"Chunk '{chunk.Id}' previously failed validation: {priorError}");

                    if (!validatedChunks.Contains(chunk.Id))
                    {
                        try
                        {
                            await ValidatePhysicalChunkAsync(chunk, chunkStore, cancellationToken);
                            validatedChunks.Add(chunk.Id);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            invalidChunks[chunk.Id] = ex.Message;
                            throw;
                        }
                    }

                    if (entry.IsNewFile)
                        await ExtractNewFileAsync(entry, chunk, chunkStore, root, cancellationToken);
                    else
                        await ExtractHdiffSegmentAsync(entry, chunk, chunkStore, root, cancellationToken);

                    extracted.Add(entry);
                    itemFinished = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"[ERROR] [{entryKind}] {entry.File}: {ex.Message}");
                    itemFinished = true;
                }
                finally
                {
                    if (itemFinished)
                        ProgressBar.Report("EXTRACT", index + 1, allFiles.Count, $"{entryKind}: {entry.File}");
                }
            }
        }
        finally
        {
            ProgressBar.End();
        }

        foreach (string error in errors)
            ProgressBar.WriteLine(error);

        ProgressBar.WriteLine($"[INFO] LDiff extraction finished. Extracted={extracted.Count:N0}; Failed={failed:N0}.\n");
        return new ExtractSummary(extracted, failed);
    }

    private static async Task ValidatePhysicalChunkAsync(
        SophonChunk chunk,
        ChunkStore chunkStore,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = chunkStore.OpenChunk(chunk.Id);
        if (stream.Length != chunk.CompressedSize)
        {
            throw new InvalidDataException(
                $"Physical chunk size mismatch for '{chunk.Id}': " +
                $"expected {chunk.CompressedSize:N0}, actual {stream.Length:N0}.");
        }

        if (!string.IsNullOrWhiteSpace(chunk.CompressedMd5))
        {
            string actualMd5 = await CalculateMd5Async(stream, cancellationToken);
            if (!actualMd5.Equals(chunk.CompressedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Physical chunk MD5 mismatch for '{chunk.Id}': " +
                    $"expected {chunk.CompressedMd5}, actual {actualMd5}.");
            }
        }
    }

    private static async Task ExtractHdiffSegmentAsync(
        SophonChunkFile entry,
        SophonChunk chunk,
        ChunkStore chunkStore,
        string root,
        CancellationToken cancellationToken)
    {
        (string outputPath, string temporaryPath) = PrepareOutputPaths(root, entry.File);
        TryDeleteFile(temporaryPath);

        try
        {
            await using FileStream input = chunkStore.OpenChunk(chunk.Id);
            input.Position = chunk.Offset;

            await using (var output = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var bounded = new BoundedReadStream(input, chunk.Length))
            {
                long copied = await CopyToAsync(bounded, output, cancellationToken);
                if (copied != chunk.Length)
                {
                    throw new InvalidDataException(
                        $"HDiff segment length mismatch for '{entry.File}': " +
                        $"expected {chunk.Length:N0}, actual {copied:N0}.");
                }
                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static async Task ExtractNewFileAsync(
        SophonChunkFile entry,
        SophonChunk chunk,
        ChunkStore chunkStore,
        string root,
        CancellationToken cancellationToken)
    {
        (string outputPath, string temporaryPath) = PrepareOutputPaths(root, entry.File);
        TryDeleteFile(temporaryPath);

        try
        {
            await using FileStream input = chunkStore.OpenChunk(chunk.Id);
            input.Position = chunk.Offset;

            int probeLength = (int)Math.Min(HeaderProbeSize, chunk.Length);
            byte[] probe = new byte[probeLength];
            await ReadExactlyAsync(input, probe, cancellationToken);

            bool isHdiff = probe.Length >= HdiffSignature.Length &&
                probe.AsSpan(0, HdiffSignature.Length).SequenceEqual(HdiffSignature);

            if (!isHdiff)
            {
                await ExtractRawNewFileAsync(
                    input, entry, chunk, temporaryPath, cancellationToken);
            }
            else
            {
                int frameOffset = FindSequence(probe, ZstdSignature, HdiffSignature.Length);
                if (frameOffset < 0)
                {
                    throw new InvalidDataException($"Zstandard frame was not found in the header of new-file segment '{entry.TargetFile}'.");
                }

                long remainingSegmentLength = chunk.Length - frameOffset;
                input.Position = checked(chunk.Offset + frameOffset);

                await using (var output = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var bounded = new BoundedReadStream(input, remainingSegmentLength))
                using (var decompressor = new DecompressionStream(bounded))
                using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
                {
                    byte[] buffer = new byte[BufferSize];
                    long written = 0;

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = await decompressor.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (read == 0)
                            break;

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        md5.AppendData(buffer, 0, read);
                        written += read;
                    }

                    await output.FlushAsync(cancellationToken);
                    string actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
                    ValidateNewFileOutput(entry, written, actualMd5);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static async Task ExtractRawNewFileAsync(
        FileStream input,
        SophonChunkFile entry,
        SophonChunk chunk,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        input.Position = chunk.Offset;

        await using var output = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bounded = new BoundedReadStream(input, chunk.Length);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        byte[] buffer = new byte[BufferSize];
        long written = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await bounded.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            md5.AppendData(buffer, 0, read);
            written += read;
        }

        await output.FlushAsync(cancellationToken);

        if (written != chunk.Length)
        {
            throw new InvalidDataException(
                $"New-file segment length mismatch for '{entry.TargetFile}': " +
                $"expected {chunk.Length:N0}, actual {written:N0}.");
        }

        string actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
        ValidateNewFileOutput(entry, written, actualMd5);
    }

    private static void ValidateNewFileOutput(
        SophonChunkFile entry,
        long actualSize,
        string actualMd5)
    {
        if (actualSize != entry.TargetSize)
        {
            throw new InvalidDataException(
                $"New-file size mismatch for '{entry.TargetFile}': " +
                $"expected {entry.TargetSize:N0}, actual {actualSize:N0}.");
        }

        if (!string.IsNullOrWhiteSpace(entry.TargetMd5) &&
            !actualMd5.Equals(entry.TargetMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"New-file MD5 mismatch for '{entry.TargetFile}': " +
                $"expected {entry.TargetMd5}, actual {actualMd5}.");
        }
    }

    private static int FindSequence(byte[] buffer, byte[] sequence, int startIndex)
    {
        for (int index = startIndex; index <= buffer.Length - sequence.Length; index++)
        {
            if (buffer.AsSpan(index, sequence.Length).SequenceEqual(sequence))
                return index;
        }
        return -1;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of physical chunk while reading a segment header.");
            totalRead += read;
        }
    }

    private static async Task<long> CopyToAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }
        return copied;
    }

    private static (string FilePath, string TemporaryPath) PrepareOutputPaths(
        string saveDirectory,
        string relativePath)
    {
        string filePath = Path.GetFullPath(Path.Combine(saveDirectory, relativePath));
        EnsurePathInsideSaveDirectory(saveDirectory, filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return (filePath, filePath + ".tmp");
    }

    private static void EnsurePathInsideSaveDirectory(string saveDirectory, string targetPath)
    {
        string root = Path.GetFullPath(saveDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest file path escapes the selected directory: {targetPath}");
        }
    }

    private static async Task<string> CalculateMd5Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var md5 = MD5.Create();
        byte[] hash = await md5.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;
            int allowed = (int)Math.Min(count, _remaining);
            int read = _inner.Read(buffer, offset, allowed);
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
                return 0;
            int allowed = (int)Math.Min(buffer.Length, _remaining);
            int read = _inner.Read(buffer[..allowed]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return 0;
            int allowed = (int)Math.Min(buffer.Length, _remaining);
            int read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
