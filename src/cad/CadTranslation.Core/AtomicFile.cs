using System.Runtime.InteropServices;
using System.Text;

namespace CadTranslation.Core;

public static class AtomicFile
{
    public static void WriteUtf8(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("A destination directory is required.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            ReplaceFile(tempPath, fullPath);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Cleanup must not replace the original write or move failure.
            }

            throw;
        }
    }

    // File.Move(overwrite) is .NET Core 3.0+. On .NET Framework use MoveFileEx so the destination is
    // replaced in one step; delete-then-move would leave a window where the target file does not exist,
    // which is exactly what an atomic writer must not do.
    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
#if NETFRAMEWORK
        if (!MoveFileEx(sourcePath, destinationPath, MOVEFILE_REPLACE_EXISTING))
        {
            throw new IOException(
                "Unable to replace " + destinationPath + " (Win32 error " + Marshal.GetLastWin32Error() + ").");
        }
#else
        File.Move(sourcePath, destinationPath, overwrite: true);
#endif
    }

#if NETFRAMEWORK
    private const int MOVEFILE_REPLACE_EXISTING = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
#endif
}
