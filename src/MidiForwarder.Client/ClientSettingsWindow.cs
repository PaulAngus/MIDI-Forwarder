using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MidiForwarder.Client;

public sealed partial class ClientSettingsWindow : Window
{
    public ClientSettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Icon = new WindowIcon(BrandAssets.Logo);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.FindControl<Button>("CloseSettingsButton")!.Click += (_, _) => Close();
        var tabs = this.FindControl<TabControl>("SettingsTabs")!;
        var saveStatus = this.FindControl<TextBlock>("SaveStatus")!;
        void RefreshFooter()
        {
            this.FindControl<Button>("SaveConnectionButton")!.IsVisible = tabs.SelectedIndex == 0;
            saveStatus.IsVisible = tabs.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(saveStatus.Text);
        }
        tabs.SelectionChanged += (_, _) => RefreshFooter();
        saveStatus.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty) { RefreshFooter(); }
        };
        SizeChanged += (_, _) =>
        {
            bool compact = ClientSize.Height < 560;
            this.FindControl<StackPanel>("DiagnosticsHeader")!.IsVisible = !compact;
            this.FindControl<Grid>("DiagnosticsPage")!.Margin = new Thickness(0, compact ? 12 : 20, 0, compact ? 12 : 20);
        };
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen is not null)
            {
                MaxHeight = Math.Max(MinHeight, Math.Min(800, screen.WorkingArea.Height / RenderScaling - 64));
            }
        };
    }
}
