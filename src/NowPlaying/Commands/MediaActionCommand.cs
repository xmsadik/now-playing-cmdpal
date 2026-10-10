using System;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace NowPlaying.Commands;

/// <summary>
/// A fire-and-forget media action. The palette/Dock stays open; the actual work (and its error handling)
/// lives in <see cref="Services.MediaService"/>.
/// </summary>
internal sealed partial class MediaActionCommand : InvokableCommand
{
    private readonly Func<Task> _action;

    public MediaActionCommand(string id, string name, IconInfo icon, Func<Task> action)
    {
        Id = id;
        Name = name;
        Icon = icon;
        _action = action;
    }

    public override ICommandResult Invoke()
    {
        _ = _action();
        return CommandResult.KeepOpen();
    }
}
