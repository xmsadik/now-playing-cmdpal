using Xunit;
using NowPlaying.Core;

namespace NowPlaying.Tests;

public class NowPlayingTextTests
{
    [Fact]
    public void Title_Trims() => Assert.Equal("Song A", NowPlayingText.Title("  Song A \r\n", "Spotify"));

    [Theory]
    [InlineData(null, "Spotify")]
    [InlineData("", "Spotify")]
    [InlineData("  ", "Spotify")]
    public void Title_FallsBackToApp(string? title, string expected) =>
        Assert.Equal(expected, NowPlayingText.Title(title, "Spotify"));

    [Fact]
    public void Title_LastResort() => Assert.Equal("Unknown track", NowPlayingText.Title(null, " "));

    [Fact]
    public void Title_CollapsesWhitespace() => Assert.Equal("A B", NowPlayingText.Title("A \n  B", null));

    [Fact]
    public void Subtitle_PrefersArtist() =>
        Assert.Equal("Artist", NowPlayingText.Subtitle(" Artist ", "Album Artist", "Spotify"));

    [Fact]
    public void Subtitle_FallsBackToAlbumArtist() =>
        Assert.Equal("Album Artist", NowPlayingText.Subtitle("", "Album Artist", "Spotify"));

    [Fact]
    public void Subtitle_FallsBackToApp() => Assert.Equal("Spotify", NowPlayingText.Subtitle(null, " ", "Spotify"));

    [Fact]
    public void Subtitle_Empty() => Assert.Equal("", NowPlayingText.Subtitle(null, null, null));

    [Fact]
    public void SessionSubtitle_JoinsAndSkipsEmpty()
    {
        Assert.Equal("Artist · Spotify · Playing", NowPlayingText.SessionSubtitle("Artist", "Spotify", "Playing"));
        Assert.Equal("Chrome · Paused", NowPlayingText.SessionSubtitle(" ", "Chrome", "Paused"));
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal("Nothing playing", NowPlayingText.NothingPlaying);
        Assert.Equal("Nothing is playing", NowPlayingText.NothingIsPlaying);
    }
}
