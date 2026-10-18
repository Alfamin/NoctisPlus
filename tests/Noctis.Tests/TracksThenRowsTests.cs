using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #36: an imported playlist's missing rows follow its tracks in the one virtualized list,
/// and every track keeps its own index (drag, selection and album runs work on Tracks).</summary>
public class TracksThenRowsTests
{
    [Fact]
    public void TracksKeepTheirIndices_AndExtraRowChangesArriveShiftedPastThem()
    {
        var tracks = new BulkObservableCollection<Track>();
        var extras = new ObservableCollection<object>();
        var rows = new TracksThenRows(tracks, extras);
        var events = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, e) => events.Add(e);

        Track a = new() { Title = "A" }, b = new() { Title = "B" };
        tracks.Add(a); tracks.Add(b);
        extras.Add("missing 1"); extras.Add("missing 2");

        Assert.Equal(4, rows.Count);
        Assert.Same(a, rows[0]); Assert.Same(b, rows[1]); Assert.Equal("missing 1", rows[2]);
        Assert.Equal(1, rows.IndexOf(b)); Assert.Equal(3, rows.IndexOf("missing 2"));
        Assert.Equal(new object[] { a, b, "missing 1", "missing 2" }, rows.ToArray());
        Assert.Equal(1, events[1].NewStartingIndex);            // a track change is passed on as is
        Assert.Equal(2, events[2].NewStartingIndex);            // the first extra row sits after both tracks
        Assert.Equal(3, events[3].NewStartingIndex);

        extras.RemoveAt(0);
        Assert.Equal(NotifyCollectionChangedAction.Remove, events[^1].Action);
        Assert.Equal(2, events[^1].OldStartingIndex);
        extras.Add("missing 3"); extras.Move(1, 0);
        Assert.Equal((3, 2), (events[^1].OldStartingIndex, events[^1].NewStartingIndex));
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)rows).Add(a));
    }
}
