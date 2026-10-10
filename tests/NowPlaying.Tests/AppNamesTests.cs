using Xunit;
using NowPlaying.Core;

namespace NowPlaying.Tests;

public class AppNamesTests
{
    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("chrome", "Chrome")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!App", "Edge")]
    [InlineData("firefox.exe", "Firefox")]
    [InlineData("308046B0AF4A39CB", "Firefox")]
    [InlineData("vlc.exe", "VLC")]
    [InlineData("foobar2000.exe", "Foobar2000")]
    [InlineData("Contoso.Player_abc123!App", "Player")]
    [InlineData("myplayer", "Myplayer")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void FriendlyName(string? id, string expected) =>
        Assert.Equal(expected, AppNames.GetFriendlyName(id));
}
