using System.Windows.Media;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Launcher;

public interface ILauncherIconProvider
{
    Task<ImageSource?> GetAsync(
        LauncherItem item,
        CancellationToken cancellationToken);
}
