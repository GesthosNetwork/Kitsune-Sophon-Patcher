# Kitsune-Sophon-Patcher

A lightweight tool for extracting Sophon LDiff update data, applying HDiffPatch updates, and verifying game files.


## Compile Instructions

1. Install the [.NET 11.0 SDK](https://dotnet.microsoft.com/download/dotnet/11.0).  
2. Run `Compile.bat`  
3. Output will be in `bin` folder.


## How to use

1. Place `SophonPatcher.exe`, the ldiff folder (containing the chunk files), and the manifest file in the same directory as the game's executable.
   For example:

```
├── ldiff/
│   ├── 8e4fa912116c2d47_73824b34872655ab42f97ecdf8a8ce34
│   ├── 94106d9dad9fd201_8ca9d67c7434395ebefaf88233c15223
│   └── ......
├── manifest_8175887eb468c10f_f99d7b450a9b375ba3de38f8b7dbbc52
├── StarRail_Data/
├── config.ini
├── GameAssembly.dll
├── HoYoKProtect.sys
├── mhypbase.dll
├── pkg_version
├── StarRail.exe
└── SophonPatcher.exe
```

```
├── ldiff/
│   ├── 74cf4ad4c3433b9a_884570691ad2c996b0be70ff6a77d285
│   ├── 7d4e53eb53c76637_812ea08b4ff526e6e57be47fae068a5e
│   └── ......
├── manifest_2ae2a243f1a9dd37_a19a2c4933f329d5105296ca893bc03c
├── ZenlessZoneZero_Data/
├── amd_ags_x64.dll
├── amd_fidelityfx_dx12.dll
├── Audio_English(US)_pkg_version
├── config.ini
├── file_category_launcher
├── GameAssembly.dll
├── HoYoKProtect.sys
├── ......
├── UnityPlayer.dll
├── version_info
├── ZenlessZoneZero.exe
├── SophonPatcher.exe
```

```
├── ldiff/
│   ├── 8986170f4b11aa26_1ef5a583d07d0761035b73e09380800e
│   ├── f2b621f3c83498d5_e0bc21d1f6722e6dcdcb0b6962337ab9
│   └── ......
├── manifest_970ab33634a4ad35_4cd820f02ceb071ff8a666caaa7e2e45
├── GenshinImpact_Data/
├── GenshinImpact.exe
├── config.ini
├── HoYoKProtect.sys
├── mhypbase.dll
├── pkg_version
├── rtlbase.dll
└── SophonPatcher.exe
```

The manifest can be named `manifest` or `manifest_<hash>_<hash>`.

ZIP, RAR, and 7z archives are supported. You can bundle the `ldiff` folder (containing the chunk files) and the `manifest` file into a single archive. When you run `SophonPatcher.exe`, the archive contents will be extracted directly into the game folder.

For example:

```
├── GenshinImpact_Data/
├── Audio_English(US)_pkg_version
├── config.ini
├── GenshinImpact.exe
├── HoYoKProtect.sys
├── mhypbase.dll
├── pkg_version
├── rtlbase.dll
├── audio_en-us_7.1.0_7.2.0_ldiff.zip
├── game_7.1.0_7.2.0_ldiff.zip
└── SophonPatcher.exe
```

Archives without LDiff + manifest are treated as regular content archives and extracted directly.

2. Run `SophonPatcher.exe`.

- Overview of the update process:

  ```text
  LDiff update
          |
          v
  Read manifest and physical chunks
          |
          v
  Extract HDiff segments and new-file payloads
          |
          v
  Apply HDiff patches to existing files
          |
          v
  Validate target size and MD5
          |
          v
  Verify package files
  ```

After an patching has been processed successfully, ldiff folder and manifest file are auto-removed to free disk space.


## Command-line reference

| Command | Description |
|---|---|
| `SophonPatcher.exe --extract` or `-e` | Reconstruct complete files from Chunk manifests or extract Patch-manifest data only; skip HDiff application and final verification. |
| `SophonPatcher.exe --verify` or `-v` | Verify game files only; do not apply patches. |
| `SophonPatcher.exe --help` or `-h` | Display available options and usage information. |

With no arguments, the application runs the full Extract chunk files and Patch workflow. If no archives are found, it looks for `ldiff` folder + manifest file.


## Notes

- All `.zip`, `.rar`, and `.7z` files beside `SophonPatcher.exe` are treated as update archives. Move unrelated archives out of the game folder before running the application.
- Each LDiff archive is processed separately because its manifest and `ldiff` cache are extracted into the same game directory.
- Extracted archive contents are written to the game directory.
- Final verification is skipped when no supported `pkg_version` files are found.
- Both Sophon Chunk manifests and Sophon Patch manifests are supported. Chunk manifests reconstruct complete files directly from their Zstandard chunks; files with missing chunks are skipped, while present chunks and reconstructed files are checksum-validated.
- Patch manifests continue to use the existing source-version selection, HDiff extraction, patch application, and verification workflow.
- Replaced protobuf-net with a focused Protocol Buffers wire reader, removing its reflection-based trim-warning source without warning suppression.
- Game and version compatibility still depends on the manifest and payload formats used by the update. Not every game or release has necessarily been tested.

## Changelog

### v1.0.2

- Replaced protobuf-net manifest deserialization with a built-in Protocol Buffers wire-format reader.
- Removed the protobuf-net dependency and its reflection-based serializer path to make manifest parsing compatible with `PublishTrimmed`.
- Retained support for both Sophon Chunk and Sophon Patch manifest formats.
- Kept the existing Chunk reconstruction, checksum validation, and Patch/HDiff workflows.


## Disclaimer

This project is developed and maintained independently. It is not affiliated with, endorsed by, or associated with HoYoverse, Cognosphere, or any game developer, publisher, or official launcher. The software is intended for personal update management, testing, research, and archival purposes. Users are responsible for obtaining and using their game update data lawfully.


## Credits

- [7-Zip](https://www.7-zip.org)
- [HDiffPatch](https://github.com/sisong/HDiffPatch)
