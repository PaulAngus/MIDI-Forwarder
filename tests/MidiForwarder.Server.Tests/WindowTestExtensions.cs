using Avalonia.Controls;

namespace MidiForwarder.Server.Tests;

internal static class WindowTestExtensions
{
    internal static T? Control<T>(this ServerWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? window.ConfigurationWindow.FindControl<T>(name);
}
