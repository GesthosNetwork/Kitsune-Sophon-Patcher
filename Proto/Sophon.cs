using ProtoBuf;

namespace SophonPatcher.Proto;

[ProtoContract]
public sealed class SophonPatchManifest
{
    [ProtoMember(1)] public List<SophonPatchFile> Patches { get; set; } = new();
    [ProtoMember(2)] public List<SophonPatchDeleteTag> DeleteTags { get; set; } = new();
    [ProtoMember(3)] public int CompressMode { get; set; }
}

[ProtoContract]
public sealed class SophonPatchFile
{
    [ProtoMember(1)] public string File { get; set; } = string.Empty;
    [ProtoMember(2)] public long Size { get; set; }
    [ProtoMember(3)] public string Md5 { get; set; } = string.Empty;
    [ProtoMember(4)] public List<SophonPatchInfo> Patches { get; set; } = new();
}

[ProtoContract]
public sealed class SophonPatchInfo
{
    [ProtoMember(1)] public string Tag { get; set; } = string.Empty;
    [ProtoMember(2)] public SophonPatch? Patch { get; set; }
}

[ProtoContract]
public sealed class SophonPatch
{
    [ProtoMember(1)] public string Id { get; set; } = string.Empty;
    [ProtoMember(2)] public string Tag { get; set; } = string.Empty;
    [ProtoMember(3)] public string BuildId { get; set; } = string.Empty;
    [ProtoMember(4)] public long PatchFileSize { get; set; }
    [ProtoMember(5)] public string PatchFileMd5 { get; set; } = string.Empty;
    [ProtoMember(6)] public long PatchOffset { get; set; }
    [ProtoMember(7)] public long PatchLength { get; set; }
    [ProtoMember(8)] public string OriginalFileName { get; set; } = string.Empty;
    [ProtoMember(9)] public long OriginalFileSize { get; set; }
    [ProtoMember(10)] public string OriginalFileMd5 { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class SophonPatchDeleteFile
{
    [ProtoMember(1)] public string File { get; set; } = string.Empty;
    [ProtoMember(2)] public long Size { get; set; }
    [ProtoMember(3)] public string Md5 { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class SophonPatchDeleteFileCollection
{
    [ProtoMember(1)] public List<SophonPatchDeleteFile> DeleteFiles { get; set; } = new();
}

[ProtoContract]
public sealed class SophonPatchDeleteTag
{
    [ProtoMember(1)] public string Tag { get; set; } = string.Empty;
    [ProtoMember(2)] public SophonPatchDeleteFileCollection? DeleteCollection { get; set; }
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

[ProtoContract]
public sealed class SophonChunkManifest
{
    [ProtoMember(1)] public List<SophonChunkManifestFile> Files { get; set; } = new();
}

[ProtoContract]
public sealed class SophonChunkManifestFile
{
    [ProtoMember(1)] public string File { get; set; } = string.Empty;
    [ProtoMember(2)] public List<SophonManifestChunk> Chunks { get; set; } = new();
    [ProtoMember(3)] public bool IsFolder { get; set; }
    [ProtoMember(4)] public long Size { get; set; }
    [ProtoMember(5)] public string Md5 { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class SophonManifestChunk
{
    [ProtoMember(1)] public string Id { get; set; } = string.Empty;
    [ProtoMember(2)] public string UncompressedMd5 { get; set; } = string.Empty;
    [ProtoMember(3)] public long Offset { get; set; }
    [ProtoMember(4)] public long CompressedSize { get; set; }
    [ProtoMember(5)] public long UncompressedSize { get; set; }
    [ProtoMember(6)] public long Unknown { get; set; }
    [ProtoMember(7)] public string CompressedMd5 { get; set; } = string.Empty;
}
