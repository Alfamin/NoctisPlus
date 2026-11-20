using Android.Content;
using Android.Provider;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// #306 Remove from Phone on Android. Plain files (the phone's own downloads and synced songs) are deleted directly.
/// A document in a folder the owner shared is deleted only when Android allows it: the folder grant is read-only
/// today, so this normally answers false and the song is hidden instead. A song found by Find All Music belongs to
/// MediaStore: the delete runs through it and Android may ask for consent (false here, the song is hidden).
/// </summary>
public sealed class AndroidSongFileRemover : ISongFileRemover
{
    private readonly Context _context;
    private readonly PlainSongFileRemover _plain = new();

    public AndroidSongFileRemover(Context context) => _context = context;

    public bool TryDelete(string path)
    {
        if (!path.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return _plain.TryDelete(path);
        try
        {
            var resolver = _context.ContentResolver;
            if (resolver == null) return false;
            var uri = global::Android.Net.Uri.Parse(path);
            if (uri == null) return false;
            if (DeviceMusicScan.IsDeviceMusicPath(path)) return resolver.Delete(uri, null, null) > 0;
            return DocumentsContract.IsDocumentUri(_context, uri) && DocumentsContract.DeleteDocument(resolver, uri);
        }
        catch (Exception ex)
        {
            // SecurityException (no write grant, or consent needed), a document that is already gone.
            DebugLog.Write("Library", $"Android did not delete the file: {ex.GetType().Name}");
            return false;
        }
    }
}