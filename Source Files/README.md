# digii_file — Arma 3 Extension

A native DLL extension for Arma 3, used by the **Zeus Ex Machina** mod to export Zeus save states to `.txt` files. This allows players to share their Zeus-placed mission compositions with others by simply sending a file.

## What It Does

The extension provides file-writing functionality that Arma 3's scripting language (SQF) cannot do on its own. When a player exports a Zeus save, the mod serializes all object data (positions, loadouts, vehicle configurations, crew, waypoints, etc.) into a string and sends it to this extension, which writes it to a `.txt` file on disk.

**The extension is write-only.** It has no file reading capability. Importing is handled entirely by SQF's native `loadFile` command — the DLL is not involved.

## Commands

The extension exposes the following commands via Arma 3's `callExtension` interface:

| Command | Type | Description |
|---------|------|-------------|
| `write_open` | Args | Creates a new `.txt` file for writing in the exports folder. Returns `ok:<full_path>` on success. |
| `write_chunk` | Args | Appends a chunk of serialized data to the currently open file. Data is sent in ~8000-char chunks due to Arma's `callExtension` size limit. |
| `write_close` | Simple | Flushes all buffered data and closes the file. |
| `list` | Simple | Returns a comma-separated list of `.txt` filenames available in the exports folder. |
| `version` | Simple | Returns the extension version string. |
| `path` | Simple | Returns the full path to the exports folder (for debugging). |

### SQF Usage Examples

```sqf
// Export a save
"digii_file" callExtension ["write_open", ["MyMission"]];
"digii_file" callExtension ["write_chunk", [_dataChunk1]];
"digii_file" callExtension ["write_chunk", [_dataChunk2]];
"digii_file" callExtension "write_close";

// List available exports
private _fileList = "digii_file" callExtension "list";
```

## Security

The extension is designed with strict security constraints:

- **Write-only** — The extension has no file reading capability whatsoever. It cannot read, access, or exfiltrate any files on the user's system.
- **Single folder** — Files can only be written to `Documents/Arma 3/DiGii_Exports/`. No other location on disk is accessible.
- **`.txt` only** — Only `.txt` files can be created. The extension enforces this regardless of what the caller requests.
- **No overwrites** — The extension cannot delete, modify, or overwrite existing files. It can only create new files. If a file with the same name already exists, the operation is rejected.
- **Path traversal protection** — All filenames are sanitized: `..`, `/`, `\`, and all invalid filename characters are stripped before any file operation.
- **No network access** — The extension makes no network connections of any kind.
- **No process/memory access** — No process manipulation or memory reading/writing outside the standard `callExtension` interface.

## Building from Source

### Prerequisites

- .NET 10 SDK (or later)
- Visual Studio 2022+ with the C++ build tools (required by NativeAOT for native linking)

### Build

Run the build script from the `Source Files` directory:

```batch
build.bat
```

This produces native DLLs for both architectures via .NET NativeAOT:

| Architecture | Output |
|---|---|
| 64-bit | `bin\x64\Release\net10.0\win-x64\publish\digii_file.dll` |
| 32-bit | `bin\x86\Release\net10.0\win-x86\publish\digii_file.dll` |

### Deployment

Copy the compiled DLLs to your Arma 3 mod root folder with the correct naming:

- 64-bit DLL → `digii_file_x64.dll`
- 32-bit DLL → `digii_file.dll`

## Technical Details

- Built with **.NET NativeAOT** — compiles C# directly into a self-contained native DLL with no .NET runtime dependency
- Uses only `System.IO` for file operations and `System.Runtime.InteropServices` for the Arma 3 extension interface
- Exports the three standard Arma 3 extension entry points: `RVExtensionVersion`, `RVExtension`, `RVExtensionArgs`
- Optimized for size (`OptimizationPreference: Size`, globalization invariant, no stack traces)

## Repository Structure

```
├── Build DLLs/            # Pre-compiled DLLs ready for deployment
│   ├── digii_file.dll     # 32-bit
│   └── digii_file_x64.dll # 64-bit
├── Source Files/           # C# source code and build files
│   ├── Extension.cs        # Extension source code
│   ├── digii_file.csproj   # .NET project file
│   └── build.bat           # Build script for both architectures
└── README.md
```

## License

This project is provided as-is for use with the Zeus Ex Machina Arma 3 mod.
