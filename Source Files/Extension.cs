// ============================================================================
// digii_file — Arma 3 Extension for Zeus Ex Machina
// ============================================================================
//
// PURPOSE:
//   This extension allows the "Zeus Ex Machina" Arma 3 mod to export saved
//   Zeus mission states (object positions, loadouts, vehicle data, etc.) to
//   plain .txt files on the user's computer. This lets players share their
//   saved Zeus scenarios with friends by simply sending a .txt file.
//
// SECURITY CONSTRAINTS:
//   - Files can ONLY be written to: Documents/Arma 3/DiGii_Exports/
//   - Files can ONLY have the .txt extension (enforced by the extension)
//   - The extension CANNOT delete files — it can only create them
//   - If a file with the same name already exists, the write is REJECTED
//   - Filenames are sanitized to prevent path traversal (no "..", no slashes)
//   - All invalid filename characters are stripped before any file operation
//
// HOW IT WORKS:
//   The mod serializes Zeus save data (arrays of object properties) into a
//   string using SQF's "str" command, then sends it to this extension in
//   chunks (~8000 characters each, due to Arma's callExtension size limit).
//   The extension writes these chunks sequentially to a .txt file.
//
//   For importing, the mod uses SQF's native "loadFile" command instead of
//   this extension, so no DLL is needed for reading files back into the game.
//
// COMMANDS (called from SQF via callExtension):
//   "digii_file" callExtension ["write_open", ["filename"]]
//       Opens a new .txt file for writing in the DiGii_Exports folder.
//       Returns "ok:<full_path>" on success, or "error:<reason>" on failure.
//       Fails if file already exists (to prevent accidental overwrites).
//
//   "digii_file" callExtension ["write_chunk", ["data..."]]
//       Appends a chunk of text data to the currently open file.
//       Returns "ok" on success, or "error:<reason>" on failure.
//
//   "digii_file" callExtension "write_close"
//       Flushes all buffered data and closes the currently open file.
//       Returns "ok" on success, or "error:no_file_open" if no file is open.
//
//   "digii_file" callExtension "list"
//       Returns a comma-separated list of .txt filenames in DiGii_Exports.
//       Used by the mod to show available exports to the user.
//
//   "digii_file" callExtension "version"
//       Returns the extension version string.
//
//   "digii_file" callExtension "path"
//       Returns the full path to the DiGii_Exports folder (for debugging).
//
// BUILD:
//   This is a .NET NativeAOT project. It compiles C# directly into a native
//   DLL with no .NET runtime dependency. The output is a self-contained
//   Windows DLL that Arma 3 can load like any other native extension.
//   Build command: dotnet publish -r win-x64 -c Release
//
// DEPLOYMENT:
//   The compiled DLL is placed in the mod root folder as "digii_file_x64.dll"
//   so Arma 3 can find and load it via the callExtension mechanism.
//
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

#nullable enable
namespace DiGiiFile;

public static class Extension
{
    // ── State ────────────────────────────────────────────────────────────
    // The StreamWriter for the file currently being written to.
    // Only one file can be open for writing at a time.
    // This is set by "write_open" and cleared by "write_close".
    private static StreamWriter? _currentWriter;

    // Cached path to the exports folder so we don't recompute it every call.
    private static string? _exportsPath;

    // ── Helper Methods ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the path to the exports folder, creating it if it doesn't exist.
    /// The folder is always: [User's Documents]/Arma 3/DiGii_Exports/
    /// This keeps exported files in an Arma 3-related location that's easy
    /// for users to find in Windows Explorer.
    /// </summary>
    private static string GetExportsPath()
    {
        if (_exportsPath == null)
        {
            // Get the user's Documents folder (e.g., C:\Users\Name\Documents)
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            // Arma 3 already stores its profiles and logs in Documents/Arma 3/
            string armaFolder = Path.Combine(documents, "Arma 3");

            // Our exports go in a subfolder to keep them organized
            _exportsPath = Path.Combine(armaFolder, "DiGii_Exports");

            // Create the folder if this is the first time we're using it
            if (!Directory.Exists(_exportsPath))
            {
                Directory.CreateDirectory(_exportsPath);
            }
        }
        return _exportsPath;
    }

    /// <summary>
    /// Sanitizes a filename to prevent security issues like path traversal.
    /// This is a critical security measure — it ensures the extension can
    /// ONLY write files inside the DiGii_Exports folder.
    ///
    /// What it does:
    ///   1. Removes all characters that are invalid in Windows filenames
    ///      (this includes / \ : * ? " < > | and control characters)
    ///   2. Removes ".." sequences to block path traversal attacks
    ///      (e.g., "../../Windows/System32/evil" becomes "WindowsSystem32evil")
    ///   3. Trims leading/trailing whitespace
    /// </summary>
    private static string SanitizeFilename(string name)
    {
        // Get the list of characters that are not allowed in filenames on this OS.
        // On Windows this includes: \ / : * ? " < > | and ASCII control chars 0-31.
        char[] invalid = Path.GetInvalidFileNameChars();

        // Remove every invalid character from the input string
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray());

        // Remove any ".." sequences to prevent directory traversal.
        // Even though we already stripped slashes above, this is an extra safety layer.
        sanitized = sanitized.Replace("..", "");

        // Trim whitespace from both ends
        return sanitized.Trim();
    }

    /// <summary>
    /// Writes a response string into Arma 3's output buffer.
    ///
    /// Arma 3 provides a fixed-size byte buffer (typically ~10KB) for the
    /// extension to write its response into. We encode our string as UTF-8
    /// bytes and copy them into the buffer, making sure to:
    ///   - Not write more bytes than the buffer can hold
    ///   - Always null-terminate the string (required by Arma 3)
    /// </summary>
    private static unsafe void WriteOutput(byte* output, int outputSize, string text)
    {
        // Convert the C# string to UTF-8 bytes
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        // Calculate how many bytes we can safely write.
        // We need to leave room for the null terminator, so max is outputSize - 1.
        int len = Math.Min(bytes.Length, outputSize - 1);

        // Copy the bytes into Arma's output buffer
        Marshal.Copy(bytes, 0, (IntPtr)output, len);

        // Null-terminate the string (Arma expects C-style null-terminated strings)
        output[len] = 0;
    }

    /// <summary>
    /// Reads a null-terminated string from an Arma 3 input buffer.
    ///
    /// Arma 3 passes command names and arguments as null-terminated ANSI
    /// strings (byte pointers). This converts them to C# strings.
    /// Returns an empty string if the pointer is null.
    /// </summary>
    private static unsafe string ReadString(byte* ptr)
    {
        return Marshal.PtrToStringAnsi((IntPtr)ptr) ?? "";
    }

    // ── Arma 3 Extension Entry Points ────────────────────────────────────
    //
    // These are the three functions that Arma 3 expects every extension DLL
    // to export. The [UnmanagedCallersOnly] attribute tells .NET NativeAOT
    // to export these as standard C functions in the compiled DLL, making
    // them callable by Arma 3's extension loading system.

    /// <summary>
    /// RVExtensionVersion — Called once by Arma 3 when the extension is first loaded.
    /// Arma displays this version in its log file to confirm the extension loaded.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RVExtensionVersion")]
    public static unsafe void RVExtensionVersion(byte* output, int outputSize)
    {
        WriteOutput(output, outputSize, "1.0.0");
    }

    /// <summary>
    /// RVExtension — Called by Arma 3 for simple commands (no arguments).
    /// SQF usage: "digii_file" callExtension "command"
    ///
    /// This handles commands that don't need any parameters:
    ///   - "list"        → returns comma-separated .txt filenames
    ///   - "write_close" → closes the currently open file
    ///   - "path"        → returns the exports folder path (debugging)
    ///   - "version"     → returns the version string
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RVExtension")]
    public static unsafe void RVExtension(byte* output, int outputSize, byte* function)
    {
        try
        {
            // Read the command name from Arma's input buffer
            string command = ReadString(function);

            switch (command)
            {
                case "list":
                    // Returns a comma-separated list of all .txt files in the exports folder.
                    // Example return: "Mission1.txt,Mission2.txt,MyScenario.txt"
                    // The mod uses this to show users which exports are available.
                    string path = GetExportsPath();
                    if (Directory.Exists(path))
                    {
                        // Find all .txt files, extract just the filename (not the full path),
                        // and join them with commas.
                        var files = Directory.GetFiles(path, "*.txt")
                            .Select(Path.GetFileName)
                            .Where(f => f != null);
                        WriteOutput(output, outputSize, string.Join(",", files));
                    }
                    else
                    {
                        // Folder doesn't exist yet — no exports available
                        WriteOutput(output, outputSize, "");
                    }
                    break;

                case "write_close":
                    // Flushes any buffered data to disk and closes the file.
                    // Must be called after write_open + write_chunk calls are done.
                    // If no file is currently open, returns an error.
                    if (_currentWriter != null)
                    {
                        _currentWriter.Flush();   // Ensure all data is written to disk
                        _currentWriter.Close();   // Close the file handle
                        _currentWriter.Dispose(); // Release all resources
                        _currentWriter = null;    // Clear the reference
                        WriteOutput(output, outputSize, "ok");
                    }
                    else
                    {
                        WriteOutput(output, outputSize, "error:no_file_open");
                    }
                    break;

                case "path":
                    // Returns the full filesystem path to the exports folder.
                    // This is only used for debugging — lets the user verify
                    // where files are being saved.
                    WriteOutput(output, outputSize, GetExportsPath());
                    break;

                case "version":
                    // Returns the extension version string.
                    WriteOutput(output, outputSize, "1.0.0");
                    break;

                default:
                    // Unknown command — return an error so the SQF side knows
                    WriteOutput(output, outputSize, "error:unknown_command");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Catch any unexpected errors and return them as a string.
            // This prevents the extension from crashing Arma 3.
            WriteOutput(output, outputSize, "error:" + ex.Message);
        }
    }

    /// <summary>
    /// RVExtensionArgs — Called by Arma 3 for commands with arguments.
    /// SQF usage: "digii_file" callExtension ["command", [arg0, arg1, ...]]
    ///
    /// This handles commands that need parameters:
    ///   - "write_open"  → opens a new file for writing
    ///   - "write_chunk" → appends data to the open file
    ///
    /// Returns 0 on success, -1 on error.
    /// The actual result message is written to the output buffer.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RVExtensionArgs")]
    public static unsafe int RVExtensionArgs(
        byte* output, int outputSize,
        byte* function,
        byte** argv, int argc)
    {
        try
        {
            // Read the command name from Arma's input buffer
            string command = ReadString(function);

            // Read all arguments into a C# string array.
            // Arma passes arguments as an array of null-terminated byte pointers.
            string[] args = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                args[i] = ReadString(argv[i]);
            }

            switch (command)
            {
                // ── write_open ─────────────────────────────────────────
                // Opens a new .txt file for writing in the DiGii_Exports folder.
                //
                // Args: [0] = desired filename (without .txt extension)
                //   The filename is typically the Zeus save state name,
                //   e.g., "My Mission" → creates "My Mission.txt"
                //
                // Returns:
                //   "ok:<full_path>"            — file opened successfully
                //   "error:no_filename"         — no filename argument provided
                //   "error:invalid_filename"    — filename was empty after sanitization
                //   "error:file_already_exists" — a file with this name already exists
                //
                // Security notes:
                //   - Filename is sanitized (no path traversal, no invalid chars)
                //   - Only .txt extension is allowed
                //   - File MUST NOT already exist (prevents overwriting)
                //   - File is always created inside DiGii_Exports folder only
                case "write_open":
                {
                    if (argc < 1)
                    {
                        WriteOutput(output, outputSize, "error:no_filename");
                        return -1;
                    }

                    // If a previous file was left open (e.g., due to an error on the SQF side),
                    // close it cleanly before opening a new one.
                    if (_currentWriter != null)
                    {
                        _currentWriter.Close();
                        _currentWriter.Dispose();
                        _currentWriter = null;
                    }

                    // Sanitize the filename to remove any dangerous characters.
                    // This prevents path traversal and ensures the file stays
                    // inside the DiGii_Exports folder.
                    string filename = SanitizeFilename(args[0]);
                    if (string.IsNullOrEmpty(filename))
                    {
                        WriteOutput(output, outputSize, "error:invalid_filename");
                        return -1;
                    }

                    // Force the .txt extension. This extension can ONLY create .txt files.
                    // This is intentional to limit the extension's capabilities for security.
                    if (!filename.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        filename += ".txt";
                    }

                    // Build the full file path (always inside DiGii_Exports)
                    string filePath = Path.Combine(GetExportsPath(), filename);

                    // SECURITY: Reject if file already exists.
                    // This prevents accidental overwrites and ensures the extension
                    // can only CREATE new files, not modify existing ones.
                    if (File.Exists(filePath))
                    {
                        WriteOutput(output, outputSize, "error:file_already_exists");
                        return -1;
                    }

                    // Create the file and open a StreamWriter for subsequent write_chunk calls
                    _currentWriter = new StreamWriter(filePath, false, Encoding.UTF8);

                    // Return success with the full path so the SQF side can show
                    // the user where the file was created
                    WriteOutput(output, outputSize, "ok:" + filePath);
                    return 0;
                }

                // ── write_chunk ────────────────────────────────────────
                // Appends a chunk of text data to the currently open file.
                //
                // Args: [0] = string data to append
                //   Arma's callExtension has a ~10KB argument size limit,
                //   so large save states are sent in multiple chunks of
                //   ~8000 characters each.
                //
                // Returns:
                //   "ok"                  — chunk written successfully
                //   "error:no_file_open"  — no file is open (call write_open first)
                //   "error:no_data"       — no data argument provided
                case "write_chunk":
                {
                    // A file must be open before we can write to it
                    if (_currentWriter == null)
                    {
                        WriteOutput(output, outputSize, "error:no_file_open");
                        return -1;
                    }
                    if (argc < 1)
                    {
                        WriteOutput(output, outputSize, "error:no_data");
                        return -1;
                    }

                    // Write the chunk to the file. The StreamWriter handles
                    // buffering internally for performance.
                    _currentWriter.Write(args[0]);
                    WriteOutput(output, outputSize, "ok");
                    return 0;
                }

                default:
                    WriteOutput(output, outputSize, "error:unknown_command");
                    return -1;
            }
        }
        catch (Exception ex)
        {
            // Catch any unexpected errors and return them as a string.
            // This prevents the extension from crashing Arma 3.
            WriteOutput(output, outputSize, "error:" + ex.Message);
            return -1;
        }
    }
}
