using System.Collections;
using System.Collections.Specialized;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// A playlist's tracks followed by extra rows (owner #36: the imported songs the library lacks), as one list
/// for the page's single virtualized ListBox. Every track keeps its own index, so drag, selection and the
/// album-run grouping that work on <c>Tracks</c> are unchanged.
/// </summary>
public sealed class TracksThenRows : IList, IReadOnlyList<object>, INotifyCollectionChanged
{
    private readonly BulkObservableCollection<Track> _tracks;
    private readonly IList _extras;

    public TracksThenRows(BulkObservableCollection<Track> tracks, IList extras)
    {
        _tracks = tracks; _extras = extras;
        tracks.CollectionChanged += (_, e) => CollectionChanged?.Invoke(this, e);
        if (extras is INotifyCollectionChanged changes) changes.CollectionChanged += (_, e) => CollectionChanged?.Invoke(this, Shift(e, _tracks.Count));
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private static NotifyCollectionChangedEventArgs Shift(NotifyCollectionChangedEventArgs e, int by) => e.Action switch
    {
        NotifyCollectionChangedAction.Add => new(e.Action, e.NewItems, e.NewStartingIndex + by),
        NotifyCollectionChangedAction.Remove => new(e.Action, e.OldItems, e.OldStartingIndex + by),
        NotifyCollectionChangedAction.Replace => new(e.Action, e.NewItems!, e.OldItems!, e.NewStartingIndex + by),
        NotifyCollectionChangedAction.Move => new(e.Action, e.NewItems, e.NewStartingIndex + by, e.OldStartingIndex + by),
        _ => new(NotifyCollectionChangedAction.Reset),
    };

    public int Count => _tracks.Count + _extras.Count;
    public object this[int index]
    {
        get => index < _tracks.Count ? _tracks[index] : _extras[index - _tracks.Count]!;
        set => throw new NotSupportedException();
    }
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }

    public IEnumerator<object> GetEnumerator()
    {
        foreach (var t in _tracks) yield return t;
        foreach (var x in _extras) yield return x!;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value)
    {
        if (value is Track t) { var i = _tracks.IndexOf(t); if (i >= 0) return i; }
        var x = _extras.IndexOf(value);
        return x < 0 ? -1 : _tracks.Count + x;
    }
    public void CopyTo(Array array, int index) { foreach (var item in this) array.SetValue(item, index++); }

    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
