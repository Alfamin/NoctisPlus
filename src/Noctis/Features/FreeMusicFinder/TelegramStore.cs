using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeMusicFinder;

/// <summary>
/// The Telegram login kept on disk: the API id and hash, the name of the signed-in account and
/// the session Telegram issued. The phone number, login code and two-step password are never
/// written. On Windows the file is encrypted with DPAPI, so only the same Windows user on the
/// same machine can read it; elsewhere it is a file only its owner can read.
/// </summary>
internal sealed class TelegramStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private byte[] _session = Array.Empty<byte>();

    public TelegramStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            var file = new FileInfo(path);
            if (file.Length > 4 * 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using var reader = new BinaryReader(new MemoryStream(Unprotect(File.ReadAllBytes(path))), Encoding.UTF8);
            ApiId = reader.ReadInt32();
            ApiHash = reader.ReadString();
            Account = reader.ReadString();
            var length = reader.ReadInt32();
            if (length < 0 || length > 4 * 1024 * 1024) throw new InvalidDataException();
            _session = reader.ReadBytes(length);
            if (_session.Length != length) throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or FormatException or ArgumentException)
        {
            // Unreadable (copied from another machine or user, or damaged): start logged out.
            ApiId = 0;
            ApiHash = Account = "";
            _session = Array.Empty<byte>();
        }
    }

    public int ApiId { get; private set; }
    public string ApiHash { get; private set; } = "";

    /// <summary>Display name of the signed-in account; empty when logged out.</summary>
    public string Account { get; private set; } = "";

    /// <summary>Starts a new login: keeps the API id and hash, drops any earlier session.</summary>
    public void Begin(int apiId, string apiHash)
    {
        lock (_gate)
        {
            ApiId = apiId;
            ApiHash = apiHash;
            Account = "";
            _session = Array.Empty<byte>();
            Save();
        }
    }

    public void SetAccount(string name)
    {
        lock (_gate)
        {
            Account = name;
            Save();
        }
    }

    /// <summary>Forgets the session. The API id and hash stay so the next login need not ask again.</summary>
    public void ClearSession()
    {
        lock (_gate)
        {
            Account = "";
            _session = Array.Empty<byte>();
            Save();
        }
    }

    /// <summary>The stream WTelegramClient reads its session from and writes it back to.</summary>
    public Stream OpenSession() => new SessionStream(this);

    private void Save()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ApiId);
            writer.Write(ApiHash);
            writer.Write(Account);
            writer.Write(_session.Length);
            writer.Write(_session);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, Protect(buffer.ToArray()));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, overwrite: true);
    }

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows() ? Dpapi(data, protect: true) : data;

    private static byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows() ? Dpapi(data, protect: false) : data;

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>
    /// WTelegramClient reads the whole session once and then rewrites it whole with a single
    /// Write whenever it changes; each of those writes goes straight to the protected file.
    /// </summary>
    private sealed class SessionStream : Stream
    {
        private readonly TelegramStore _store;

        public SessionStream(TelegramStore store) => _store = store;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _store._session.Length;
        public override long Position { get => 0; set { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var session = _store._session;
            count = Math.Min(count, session.Length);
            Array.Copy(session, 0, buffer, offset, count);
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_store._gate)
            {
                _store._session = buffer.AsSpan(offset, count).ToArray();
                _store.Save();
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
    }
}
