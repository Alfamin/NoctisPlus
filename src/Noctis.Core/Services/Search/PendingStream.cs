using Noctis.Services.Search;
namespace Noctis.ViewModels;
/// <summary>What a stand-in in the queue is (owner #95): a playlist song not on this computer yet.</summary>
/// <param name="Page">Owner #100: the page it was played from ("playlist:…", "album:…", "artist:…", "foryou"); its header toggle
/// decides whether that page's songs you don't have play.</param>
public sealed record PendingStream(IOnlineMusicSearch Source, OnlineSong Song, bool Keep, string? Page = null);
