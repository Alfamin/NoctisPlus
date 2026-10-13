using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Noctis.Helpers;

/// <summary>
/// A Windows rename with POSIX semantics: the target name moves to the new file at once,
/// and handles still open on the old file keep reading the old data until they close,
/// the way rename(2) behaves on macOS/Linux. File.Move (MoveFileEx REPLACE_EXISTING)
/// refuses to replace a file that has any handle open, even one shared for delete.
/// This one only needs every handle open on the target to share delete (LibVLC's
/// handle on the playing track does) and a volume that supports it (NTFS; not FAT,
/// exFAT or most network shares, which fail it so the caller falls back).
/// </summary>
internal static class WindowsPosixRename
{
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4; // read | write | delete
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfoHeader
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        // WCHAR FileName[] follows
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="target"/>. False (with the
    /// Win32 error) when Windows refuses: a handle on the target not shared for delete
    /// (ERROR_SHARING_VIOLATION), a read-only target (ERROR_ACCESS_DENIED), or a volume
    /// without POSIX rename (ERROR_INVALID_PARAMETER / ERROR_NOT_SUPPORTED).
    /// </summary>
    public static bool TryReplace(string source, string target, out int error)
    {
        error = 0;
        if (!OperatingSystem.IsWindows()) { error = 50; return false; } // ERROR_NOT_SUPPORTED

        using var handle = CreateFile(Path.GetFullPath(source), Delete | Synchronize, ShareAll, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var name = Path.GetFullPath(target);
        var nameOffset = Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.FileNameLength)).ToInt32() + sizeof(uint);
        var size = Math.Max(nameOffset + (name.Length + 1) * sizeof(char), Marshal.SizeOf<FileRenameInfoHeader>());
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.StructureToPtr(new FileRenameInfoHeader
            {
                Flags = ReplaceIfExists | PosixSemantics,
                RootDirectory = IntPtr.Zero,
                FileNameLength = (uint)(name.Length * sizeof(char)),
            }, buffer, false);
            Marshal.Copy(name.ToCharArray(), 0, buffer + nameOffset, name.Length);
            if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size))
                return true;
            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The Win32 error's text, for the log.</summary>
    public static string Describe(int error) => new Win32Exception(error).Message;
}
