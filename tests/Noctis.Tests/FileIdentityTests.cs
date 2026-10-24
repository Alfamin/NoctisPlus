using Noctis.Features.TrueShuffle;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #154: a shuffle reads every song's file identity; it must not open the songs for reading.</summary>
public class FileIdentityTests
{
    [Fact]
    public void TheIdentity_IsStable_SurvivesARename_AndNeedsNoReadAccess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("noctis-identity-").FullName;
        try
        {
            var path = Path.Combine(dir, "song.flac");
            File.WriteAllBytes(path, new byte[1024]);
            var identity = FileIdentity.Read(path);
            Assert.StartsWith("winfile:", identity);
            Assert.Equal(identity, FileIdentity.Read(path));
            var moved = Path.Combine(dir, "renamed.flac");
            File.Move(path, moved);
            Assert.Equal(identity, FileIdentity.Read(moved)); // same-volume rename keeps it

            // Another program holding the file with no sharing at all: attributes are still readable.
            using (new FileStream(moved, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(identity, FileIdentity.Read(moved));

            Assert.Null(FileIdentity.Read(dir));
            Assert.Null(FileIdentity.Read(Path.Combine(dir, "missing.flac")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
