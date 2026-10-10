using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using NowPlaying.Pages;
using NowPlaying.Services;

namespace NowPlaying;

public partial class NowPlayingCommandsProvider : CommandProvider
{
    private readonly MediaService _service = new();
    private readonly SessionsPage _page;
    private readonly NowPlayingDockBand _band;
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _bands;

    public NowPlayingCommandsProvider()
    {
        DisplayName = "Now Playing";
        Id = "NowPlaying";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _page = new SessionsPage(_service);
        _band = new NowPlayingDockBand(_service);

        _commands =
        [
            new CommandItem(_page)
            {
                Title = DisplayName,
                Subtitle = "Control the media that is playing",
            },
        ];
        _bands = [_band.Band];

        _service.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _bands;

    public override void Dispose()
    {
        _band.Dispose();
        _page.Dispose();
        _service.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
