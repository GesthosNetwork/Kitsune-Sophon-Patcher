namespace SophonPatcher.Proto;

public sealed class SophonPatchManifest
{
    public List<SophonPatchFile> Patches { get; set; } = new();
    public List<SophonPatchDeleteTag> DeleteTags { get; set; } = new();
    public int CompressMode { get; set; }
}

public sealed class SophonPatchFile
{
    public string File { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Md5 { get; set; } = string.Empty;
    public List<SophonPatchInfo> Patches { get; set; } = new();
}

public sealed class SophonPatchInfo
{
    public string Tag { get; set; } = string.Empty;
    public SophonPatch? Patch { get; set; }
}

public sealed class SophonPatch
{
    public string Id { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string BuildId { get; set; } = string.Empty;
    public long PatchFileSize { get; set; }
    public string PatchFileMd5 { get; set; } = string.Empty;
    public long PatchOffset { get; set; }
    public long PatchLength { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public long OriginalFileSize { get; set; }
    public string OriginalFileMd5 { get; set; } = string.Empty;
}

public sealed class SophonPatchDeleteFile
{
    public string File { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Md5 { get; set; } = string.Empty;
}

public sealed class SophonPatchDeleteFileCollection
{
    public List<SophonPatchDeleteFile> DeleteFiles { get; set; } = new();
}

public sealed class SophonPatchDeleteTag
{
    public string Tag { get; set; } = string.Empty;
    public SophonPatchDeleteFileCollection? DeleteCollection { get; set; }
}

public sealed class SophonChunkFile
{
    public string File { get; set; } = string.Empty;
    public bool IsChunkManifestFile { get; set; }
    public string TargetFile { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public bool IsNewFile { get; set; }
    public bool IsFolder { get; set; }

    public long TargetSize { get; set; }
    public string TargetMd5 { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;
    public long OriginalFileSize { get; set; }
    public string OriginalFileMd5 { get; set; } = string.Empty;

    public List<SophonChunk> Chunks { get; } = new();
}

public sealed class SophonChunk
{
    public string Id { get; set; } = string.Empty;
    public long CompressedSize { get; set; }
    public string CompressedMd5 { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long Length { get; set; }
    public long UncompressedSize { get; set; }
    public string UncompressedMd5 { get; set; } = string.Empty;
    public long Unknown { get; set; }
}

// Native Sophon Chunk manifest data models.
public sealed class SophonChunkManifest
{
    public List<SophonChunkManifestFile> Files { get; set; } = new();
}

public sealed class SophonChunkManifestFile
{
    public string File { get; set; } = string.Empty;
    public List<SophonManifestChunk> Chunks { get; set; } = new();
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public string Md5 { get; set; } = string.Empty;
}

public sealed class SophonManifestChunk
{
    public string Id { get; set; } = string.Empty;
    public string UncompressedMd5 { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long CompressedSize { get; set; }
    public long UncompressedSize { get; set; }
    public long Unknown { get; set; }
    public string CompressedMd5 { get; set; } = string.Empty;
}
