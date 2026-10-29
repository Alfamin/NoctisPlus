using System;
using System.IO;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// .m4b (Discord, Luwi): Apple's audiobook file is an M4A under another name — the same MP4
/// container, usually AAC, plus chapters. It was not on any extension list, so a folder of
/// audiobooks scanned as empty and "Open with" / drag-drop ignored the files.
/// </summary>
public class M4bSupportTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", name);

    [Fact]
    public void Whitelist_ContainsM4b() =>
        Assert.Contains(".m4b", MetadataService.SupportedExtensions);

    [Fact]
    public void WindowsOpenWith_OffersM4b()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains(".m4b", WindowsFileAssociations.Extensions);
    }

    [Fact]
    public void Installer_RegistersM4bLikeEveryOtherOfferedExtension()
    {
        if (!OperatingSystem.IsWindows()) return;
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer.iss"));
        foreach (var ext in WindowsFileAssociations.Extensions)
        {
            Assert.Contains($"ValueName: \"{ext}\"; ValueData: \"{ProductIdentity.ProgId}\"", iss);
            Assert.Contains($"Subkey: \"Software\\Classes\\{ext}\\OpenWithProgids\"", iss);
        }
    }

    [Fact]
    public void TagRead_OpensAnAacAudiobook()
    {
        // 3 s mono AAC, -f ipod -brand "M4B ", three chapters (ffmpeg ffmetadata).
        var track = new MetadataService().ReadTrackMetadata(Fixture("aac_chapters_tiny.m4b"));

        Assert.NotNull(track);
        Assert.Equal("Tiny Audiobook", track!.Title);
        Assert.Equal("Noctis Narrator", track.Artist);
        Assert.Equal("Tiny Audiobook", track.Album);
        Assert.Equal("AAC", track.CodecShortName);
        Assert.False(track.IsLossless);
        Assert.InRange(track.Duration.TotalSeconds, 2.5, 3.5);
    }

    [Fact]
    public void FileInfo_NamesTheAudiobookFormat()
    {
        var info = new MetadataService().ReadFileInfo(Fixture("aac_chapters_tiny.m4b"));

        Assert.NotNull(info);
        Assert.Equal("Apple Audiobook (M4B)", info!.FileFormat);
        Assert.False(info.IsLossless);
    }

    [Fact]
    public void TagWrite_RoundTripsOnAnAudiobook()
    {
        var copy = Path.Combine(Path.GetTempPath(), $"noctis-m4b-{Guid.NewGuid():N}.m4b");
        File.Copy(Fixture("aac_chapters_tiny.m4b"), copy);
        try
        {
            var service = new MetadataService();
            var track = service.ReadTrackMetadata(copy)!;
            track.Title = "Renamed Book";
            Assert.True(service.WriteTrackMetadata(track));
            Assert.Equal("Renamed Book", service.ReadTrackMetadata(copy)!.Title);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Theory]
    [InlineData("", "AAC", false)]
    [InlineData("Apple Lossless", "ALAC", true)]
    [InlineData("MPEG-4 AAC", "AAC", false)]
    public void Badge_TreatsM4bLikeM4a(string codec, string shortName, bool lossless)
    {
        var track = new Track { FilePath = "book.m4b", Codec = codec };
        Assert.Equal(shortName, track.CodecShortName);
        Assert.Equal(lossless, track.IsLossless);
    }

    [Fact]
    public void Server_StreamsM4bAsMp4Audio() =>
        Assert.Equal("audio/mp4", NoctisServer.ContentTypeFor("book.m4b"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Noctis.sln not found above the test output");
    }
}
