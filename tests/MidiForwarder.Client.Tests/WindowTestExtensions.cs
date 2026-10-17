using Avalonia.Controls;

namespace MidiForwarder.Client.Tests;

internal static class WindowTestExtensions
{
    internal static T? Control<T>(this ClientWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? window.ConfigurationWindow.FindControl<T>(name);
}
