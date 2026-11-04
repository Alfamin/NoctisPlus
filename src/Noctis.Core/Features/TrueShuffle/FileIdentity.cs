// True Shuffle domain logic, from Alfamin/TrueShuffle 57114ad (MIT, same author), built into Noctis Plus.
// Pure: no plugin hooks, reflection or UI. The native player integration uses it directly.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Noctis.Features.TrueShuffle;

/// <summary>Read-only Windows file identity; unchanged by a same-volume move or rename.</summary>
/// <remarks>Owner #154: opened for its attributes only. Opening every song to read (a shuffle reads the whole library)
/// made the first shuffle after a start take ~10 s: the antivirus scans each file opened for reading, ~4.2 ms a cold
/// file against ~0.03 ms for attributes (measured on 119 cold FLACs; same file index both ways).</remarks>
internal static class FileIdentity
{
    private const uint ReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;

    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = CreateFileW(Path.GetFullPath(path), ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0) return null;
            ulong index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
            if (index == 0) return null;
            return $"winfile:{info.Volume:X8}:{index:X16}:{info.CreatedHigh:X8}{info.CreatedLow:X8}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);
}
