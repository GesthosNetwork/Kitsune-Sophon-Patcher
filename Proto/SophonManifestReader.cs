using System.Text;

namespace SophonPatcher.Proto;

public static class SophonManifestReader
{
    public static SophonChunkManifest ParseChunkManifest(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var manifest = new SophonChunkManifest();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    manifest.Files.Add(ParseChunkFile(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return manifest;
    }

    public static SophonPatchManifest ParsePatchManifest(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var manifest = new SophonPatchManifest();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    manifest.Patches.Add(ParsePatchFile(reader.ReadLengthDelimited()));
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 2);
                    manifest.DeleteTags.Add(ParseDeleteTag(reader.ReadLengthDelimited()));
                    break;
                case 3:
                    RequireWireType(fieldNumber, wireType, 0);
                    manifest.CompressMode = reader.ReadInt32();
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return manifest;
    }

    private static SophonChunkManifestFile ParseChunkFile(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var file = new SophonChunkManifestFile();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.File = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.Chunks.Add(ParseManifestChunk(reader.ReadLengthDelimited()));
                    break;
                case 3:
                    RequireWireType(fieldNumber, wireType, 0);
                    file.IsFolder = reader.ReadBoolean();
                    break;
                case 4:
                    RequireWireType(fieldNumber, wireType, 0);
                    file.Size = reader.ReadInt64();
                    break;
                case 5:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.Md5 = reader.ReadString();
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return file;
    }

    private static SophonManifestChunk ParseManifestChunk(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var chunk = new SophonManifestChunk();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    chunk.Id = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 2);
                    chunk.UncompressedMd5 = reader.ReadString();
                    break;
                case 3:
                    RequireWireType(fieldNumber, wireType, 0);
                    chunk.Offset = reader.ReadInt64();
                    break;
                case 4:
                    RequireWireType(fieldNumber, wireType, 0);
                    chunk.CompressedSize = reader.ReadInt64();
                    break;
                case 5:
                    RequireWireType(fieldNumber, wireType, 0);
                    chunk.UncompressedSize = reader.ReadInt64();
                    break;
                case 6:
                    RequireWireType(fieldNumber, wireType, 0);
                    chunk.Unknown = reader.ReadInt64();
                    break;
                case 7:
                    RequireWireType(fieldNumber, wireType, 2);
                    chunk.CompressedMd5 = reader.ReadString();
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return chunk;
    }

    private static SophonPatchFile ParsePatchFile(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var file = new SophonPatchFile();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.File = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 0);
                    file.Size = reader.ReadInt64();
                    break;
                case 3:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.Md5 = reader.ReadString();
                    break;
                case 4:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.Patches.Add(ParsePatchInfo(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return file;
    }

    private static SophonPatchInfo ParsePatchInfo(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var patchInfo = new SophonPatchInfo();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    patchInfo.Tag = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 2);
                    patchInfo.Patch = ParsePatch(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return patchInfo;
    }

    private static SophonPatch ParsePatch(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var patch = new SophonPatch();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    patch.Id = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 2);
                    patch.Tag = reader.ReadString();
                    break;
                case 3:
                    RequireWireType(fieldNumber, wireType, 2);
                    patch.BuildId = reader.ReadString();
                    break;
                case 4:
                    RequireWireType(fieldNumber, wireType, 0);
                    patch.PatchFileSize = reader.ReadInt64();
                    break;
                case 5:
                    RequireWireType(fieldNumber, wireType, 2);
                    patch.PatchFileMd5 = reader.ReadString();
                    break;
                case 6:
                    RequireWireType(fieldNumber, wireType, 0);
                    patch.PatchOffset = reader.ReadInt64();
                    break;
                case 7:
                    RequireWireType(fieldNumber, wireType, 0);
                    patch.PatchLength = reader.ReadInt64();
                    break;
                case 8:
                    RequireWireType(fieldNumber, wireType, 2);
                    patch.OriginalFileName = reader.ReadString();
                    break;
                case 9:
                    RequireWireType(fieldNumber, wireType, 0);
                    patch.OriginalFileSize = reader.ReadInt64();
                    break;
                case 10:
                    RequireWireType(fieldNumber, wireType, 2);
                    patch.OriginalFileMd5 = reader.ReadString();
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return patch;
    }

    private static SophonPatchDeleteTag ParseDeleteTag(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var deleteTag = new SophonPatchDeleteTag();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    deleteTag.Tag = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 2);
                    deleteTag.DeleteCollection = ParseDeleteFileCollection(reader.ReadLengthDelimited());
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return deleteTag;
    }

    private static SophonPatchDeleteFileCollection ParseDeleteFileCollection(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var collection = new SophonPatchDeleteFileCollection();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            if (fieldNumber == 1)
            {
                RequireWireType(fieldNumber, wireType, 2);
                collection.DeleteFiles.Add(ParseDeleteFile(reader.ReadLengthDelimited()));
            }
            else
            {
                reader.SkipField(fieldNumber, wireType);
            }
        }

        return collection;
    }

    private static SophonPatchDeleteFile ParseDeleteFile(ReadOnlySpan<byte> data)
    {
        var reader = new ProtobufWireReader(data);
        var file = new SophonPatchDeleteFile();

        while (reader.TryReadField(out int fieldNumber, out int wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.File = reader.ReadString();
                    break;
                case 2:
                    RequireWireType(fieldNumber, wireType, 0);
                    file.Size = reader.ReadInt64();
                    break;
                case 3:
                    RequireWireType(fieldNumber, wireType, 2);
                    file.Md5 = reader.ReadString();
                    break;
                default:
                    reader.SkipField(fieldNumber, wireType);
                    break;
            }
        }

        return file;
    }

    private static void RequireWireType(int fieldNumber, int actualWireType, int expectedWireType)
    {
        if (actualWireType != expectedWireType)
        {
            throw new InvalidDataException(
                $"Protobuf field {fieldNumber} has wire type {actualWireType}; expected {expectedWireType}.");
        }
    }

    private ref struct ProtobufWireReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ProtobufWireReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public bool TryReadField(out int fieldNumber, out int wireType)
        {
            if (_position >= _data.Length)
            {
                fieldNumber = 0;
                wireType = 0;
                return false;
            }

            ulong tag = ReadVarint();
            ulong rawFieldNumber = tag >> 3;
            if (tag == 0 || rawFieldNumber == 0 || rawFieldNumber > 536_870_911)
                throw new InvalidDataException("The protobuf manifest contains an invalid field tag.");

            fieldNumber = (int)rawFieldNumber;
            wireType = (int)(tag & 7);
            if (wireType is 6 or 7)
                throw new InvalidDataException($"The protobuf manifest contains unsupported wire type {wireType}.");

            return true;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;

            for (int shift = 0; shift < 70; shift += 7)
            {
                if (_position >= _data.Length)
                    throw new InvalidDataException("The protobuf manifest ended inside a varint.");

                byte current = _data[_position++];
                if (shift == 63 && (current & 0xFE) != 0)
                    throw new InvalidDataException("The protobuf manifest contains an overflowing varint.");

                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return value;
            }

            throw new InvalidDataException("The protobuf manifest contains an invalid varint.");
        }

        public int ReadInt32() => unchecked((int)ReadVarint());

        public long ReadInt64() => unchecked((long)ReadVarint());

        public bool ReadBoolean() => ReadVarint() != 0;

        public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            ulong rawLength = ReadVarint();
            if (rawLength > (ulong)(_data.Length - _position))
                throw new InvalidDataException("A protobuf length-delimited field exceeds the remaining manifest data.");

            int length = (int)rawLength;
            ReadOnlySpan<byte> value = _data.Slice(_position, length);
            _position += length;
            return value;
        }

        public void SkipField(int fieldNumber, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    _ = ReadVarint();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    _ = ReadLengthDelimited();
                    break;
                case 3:
                    SkipGroup(fieldNumber);
                    break;
                case 4:
                    throw new InvalidDataException("The protobuf manifest contains an unexpected end-group tag.");
                case 5:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException($"The protobuf manifest contains unsupported wire type {wireType}.");
            }
        }

        private void SkipGroup(int groupFieldNumber)
        {
            while (TryReadField(out int fieldNumber, out int wireType))
            {
                if (wireType == 4)
                {
                    if (fieldNumber != groupFieldNumber)
                        throw new InvalidDataException("The protobuf manifest contains a mismatched end-group tag.");
                    return;
                }

                SkipField(fieldNumber, wireType);
            }

            throw new InvalidDataException("The protobuf manifest ended inside a group field.");
        }

        private void Advance(int count)
        {
            if (count > _data.Length - _position)
                throw new InvalidDataException("A fixed-width protobuf field exceeds the remaining manifest data.");

            _position += count;
        }
    }
}
