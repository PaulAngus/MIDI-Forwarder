using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MidiForwarder;

internal static class BrandAssets
{
    public static Bitmap Logo { get; } = LoadLogo();

    public static Grid CreateHeader(string title)
    {
        var image = new Image
        {
            Source = Logo,
            Width = 36,
            Height = 36,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        var label = new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("36,*"),
            Children = { image, label },
        };
    }

    private static Bitmap LoadLogo()
    {
        string? assembly = typeof(BrandAssets).Assembly.GetName().Name;
        using Stream stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/midi-forwarder-256.png"));
        return new Bitmap(stream);
    }
}

