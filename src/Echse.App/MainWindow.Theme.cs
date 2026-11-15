using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Echse.App;

public sealed partial class MainWindow
{
    private ThemePreviewOptions themePreview = new();
    private static ThemePreviewMetrics ThemeMetrics =>
        (ThemePreviewMetrics)Application.Current.Resources["LauncherThemeMetrics"];
    private static SolidColorBrush MainText => (SolidColorBrush)Application.Current.Resources["LauncherTextBrush"];
    private static SolidColorBrush OnAction => (SolidColorBrush)Application.Current.Resources["LauncherOnActionBrush"];

    private sealed class ThemePreviewOptions
    {
        public Color Background = Color.FromArgb(255, 9, 9, 11);
        public Color Panel = Color.FromArgb(255, 41, 42, 46);
        public Color Sidebar = Color.FromArgb(255, 16, 17, 20);
        public Color Popup = Color.FromArgb(255, 32, 33, 37);
        public Color Accent = Color.FromArgb(255, 255, 75, 85);
        public Color Action = Color.FromArgb(255, 213, 31, 48);
        public Color Text = Color.FromArgb(255, 244, 244, 245);
        public Color Muted = Color.FromArgb(255, 200, 201, 207);
        public Color OnAction = Colors.White;
        public Color Border = Color.FromArgb(255, 86, 87, 94);
        public Color AcrylicTint = Color.FromArgb(255, 32, 32, 32);
        public double BackgroundOpacity = 0.22;
        public double PanelOpacity = 0.34;
        public double SidebarOpacity = 0.38;
        public double ControlOpacity = 0.30;
        public double ActionOpacity = 0.92;
        public double PopupOpacity = 0.94;
        public double BorderOpacity = 0.55;
        public double TintOpacity = 0.12;
        public double LuminosityOpacity = 0.08;
        public double Radius = 10;
        public bool Acrylic = true;
    }

    private void InitializeThemePreview()
    {
        Root.SizeChanged += (_, _) => ArrangeThemePreview();
        var shortcut = new KeyboardAccelerator { Key = VirtualKey.F8 };
        shortcut.Invoked += (_, args) =>
        {
            ToggleThemePreview();
            args.Handled = true;
        };
        Root.KeyboardAccelerators.Add(shortcut);
        ApplyThemePreview();
    }

    private void ToggleThemePreview()
    {
        if (ThemePreviewPane.Visibility == Visibility.Visible)
        {
            ThemePreviewPane.Visibility = Visibility.Collapsed;
            return;
        }
        BuildThemePreview();
        ThemePreviewPane.Visibility = Visibility.Visible;
        ArrangeThemePreview();
    }

    private void ArrangeThemePreview()
    {
        var dockRight = Root.ActualWidth >= 1080;
        Grid.SetRow(ThemePreviewPane, dockRight ? 1 : 2);
        Grid.SetColumn(ThemePreviewPane, dockRight ? 1 : 0);
        Grid.SetColumnSpan(ThemePreviewPane, dockRight ? 1 : 2);
        ThemePreviewPane.Width = dockRight ? 360 : double.NaN;
        ThemePreviewPane.Height = dockRight ? double.NaN : 320;
        ThemePreviewPane.Margin = dockRight ? new Thickness(0, 8, 8, 8) : new Thickness(12, 0, 12, 12);
    }

    private void ApplyThemePreview()
    {
        var resources = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries["Dark"];
        void Paint(Color color, params string[] names)
        {
            foreach (var name in names)
                if (resources.TryGetValue(name, out var value) && value is SolidColorBrush brush)
                    brush.Color = color;
                else
                    resources[name] = new SolidColorBrush(color);
        }
        var nativeAcrylic = themePreview.Acrylic && DesktopAcrylicController.IsSupported();
        var background = Opacity(themePreview.Background, nativeAcrylic ? themePreview.BackgroundOpacity : 1);
        var sidebar = Opacity(themePreview.Sidebar, themePreview.SidebarOpacity);
        var panel = Opacity(themePreview.Panel, themePreview.PanelOpacity);
        var popup = Opacity(themePreview.Popup, themePreview.PopupOpacity);
        var hover = Mix(themePreview.Action, Colors.White, 0.12);
        var pressed = Mix(themePreview.Action, Colors.Black, 0.20);
        Paint(background, "LauncherSurfaceTint");
        Paint(sidebar, "LauncherLibrarySidebarBrush", "LauncherLibraryOverlayBrush");
        Paint(panel, "LauncherPanelBrush");
        Paint(popup, "LauncherDialogBrush", "ContentDialogBackground", "ContentDialogTopOverlay",
            "SolidBackgroundFillColorBaseBrush", "SolidBackgroundFillColorSecondaryBrush",
            "FlyoutPresenterBackground", "ComboBoxDropDownBackground", "MenuFlyoutPresenterBackground");
        Paint(Opacity(themePreview.Border, themePreview.BorderOpacity), "LauncherBorderBrush", "ControlStrokeColorDefaultBrush");
        Paint(themePreview.Text, "LauncherTextBrush", "TextFillColorPrimaryBrush",
            "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused");
        Paint(themePreview.Muted, "LauncherMutedBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush");
        Paint(Opacity(themePreview.Text, 0.45), "TextFillColorDisabledBrush", "TextControlForegroundDisabled");
        Paint(themePreview.Accent, "LauncherAccentBrush", "AccentTextFillColorPrimaryBrush",
            "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush");
        Paint(themePreview.OnAction, "LauncherOnActionBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver",
            "AccentButtonForegroundPressed", "TextOnAccentFillColorPrimaryBrush");
        Paint(Opacity(themePreview.Action, themePreview.ActionOpacity), "LauncherActionBrush",
            "AccentButtonBackground", "AccentFillColorDefaultBrush");
        Paint(Opacity(hover, themePreview.ActionOpacity), "LauncherActionHoverBrush",
            "AccentButtonBackgroundPointerOver", "AccentFillColorSecondaryBrush");
        Paint(Opacity(pressed, themePreview.ActionOpacity), "LauncherActionPressedBrush",
            "AccentButtonBackgroundPressed", "AccentFillColorTertiaryBrush");
        Paint(Opacity(themePreview.Panel, themePreview.ControlOpacity), "ControlFillColorDefaultBrush",
            "ControlFillColorSecondaryBrush", "ControlFillColorTertiaryBrush", "TextControlBackground");
        Paint(Opacity(themePreview.Panel, Math.Min(1, themePreview.ControlOpacity + 0.12)), "TextControlBackgroundPointerOver");
        Paint(Opacity(themePreview.Panel, Math.Min(1, themePreview.ControlOpacity + 0.20)), "TextControlBackgroundFocused");
        Paint(panel, "ListViewItemBackgroundSelected");
        Paint(Opacity(themePreview.Panel, Math.Min(1, themePreview.PanelOpacity + 0.12)), "ListViewItemBackgroundSelectedPointerOver");
        resources["SystemAccentColor"] = themePreview.Action;
        resources["SystemAccentColorLight1"] = hover;
        resources["SystemAccentColorLight2"] = themePreview.Accent;
        resources["SystemAccentColorLight3"] = themePreview.Muted;
        var gradient = (LinearGradientBrush)resources["LauncherGradientBrush"];
        gradient.GradientStops[0].Color = background;
        gradient.GradientStops[1].Color = sidebar;
        var navigation = (ResourceDictionary)Navigation.Resources.ThemeDictionaries["Dark"];
        ((SolidColorBrush)navigation["NavigationViewTopPaneBackground"]).Color = sidebar;
        ((SolidColorBrush)navigation["NavigationViewSelectionIndicatorForeground"]).Color = themePreview.Accent;
        if (nativeAcrylic)
        {
            if (SystemBackdrop is not NeutralAcrylicBackdrop) SystemBackdrop = new NeutralAcrylicBackdrop();
            ((NeutralAcrylicBackdrop)SystemBackdrop).Update(themePreview.AcrylicTint, themePreview.TintOpacity,
                themePreview.LuminosityOpacity, themePreview.Sidebar);
        }
        else SystemBackdrop = null;
        ApplyThemeRadius();
    }

    private void ApplyThemeRadius()
    {
        if (ThemeMetrics.Corners.TopLeft == themePreview.Radius) return;
        var corners = new CornerRadius(themePreview.Radius);
        foreach (var key in new[] { "LauncherCornerRadius", "ControlCornerRadius", "OverlayCornerRadius",
                     "ListViewItemCornerRadius", "GridViewItemCornerRadius" })
            Application.Current.Resources[key] = corners;
        ThemeMetrics.Corners = corners;
        ThemeMetrics.TopCorners = new CornerRadius(themePreview.Radius, themePreview.Radius, 0, 0);
        BindInputCorners(Root);
        if (activeDialog is not null) BindInputCorners(activeDialog);
        SizePageContent();
    }

    private static void BindInputCorners(DependencyObject parent)
    {
        if (parent is Microsoft.UI.Xaml.Controls.Button or TextBox or PasswordBox or ComboBox or ListViewItem or GridViewItem or ContentDialog or Expander)
            ThemeCorners((Control)parent);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            BindInputCorners(VisualTreeHelper.GetChild(parent, index));
    }

    private static T ThemeCorners<T>(T element, bool topOnly = false) where T : FrameworkElement
    {
        var property = element switch
        {
            Border => Border.CornerRadiusProperty,
            Grid => Grid.CornerRadiusProperty,
            Control => Control.CornerRadiusProperty,
            _ => throw new ArgumentException("Dieses Element unterstützt keine Theme-Rundungen.")
        };
        element.SetBinding(property, new Binding
        {
            Source = ThemeMetrics, Mode = BindingMode.OneWay,
            Path = new PropertyPath(topOnly ? nameof(ThemePreviewMetrics.TopCorners) : nameof(ThemePreviewMetrics.Corners))
        });
        return element;
    }

    private void BuildThemePreview()
    {
        var white = new SolidColorBrush(Colors.White);
        TextBlock Label(string text, double size = 13) => Text(text, size, white);
        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(Label("Theme-Labor", 21));
        title.Children.Add(Label("Nur diese Sitzung · F8 zum Ein-/Ausblenden", 11));
        header.Children.Add(title);
        var close = Button("×", () => { ToggleThemePreview(); return Task.CompletedTask; });
        close.Foreground = white;
        AutomationProperties.SetName(close, "Theme-Labor schließen");
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);
        var reset = Button("Standard wiederherstellen", () =>
        {
            themePreview = new ThemePreviewOptions();
            ApplyThemePreview();
            BuildThemePreview();
            return Task.CompletedTask;
        });
        reset.Background = new SolidColorBrush(Color.FromArgb(255, 60, 60, 65));
        reset.Foreground = white;
        Grid.SetRow(reset, 1);
        root.Children.Add(reset);
        var controls = new StackPanel { Spacing = 16, Margin = new Thickness(0, 0, 12, 0) };
        controls.Children.Add(Label("Live-Vorschau ohne Speichern. Du kannst weiterhin zwischen allen Seiten wechseln. Schließen behält die Vorschau, ein Neustart verwirft sie."));
        controls.Children.Add(Label("DECKKRAFT · weniger = durchsichtiger", 12));
        void Amount(string title, double value, Action<double> set, bool percent = true)
        {
            var slider = new Slider
            {
                Minimum = 0, Maximum = percent ? 100 : 24, StepFrequency = 1,
                Value = percent ? Math.Round(value * 100) : value, Foreground = white
            };
            string Caption(double amount) => $"{title} · {amount:N0}{(percent ? " %" : " px")}";
            slider.Header = Label(Caption(slider.Value));
            AutomationProperties.SetName(slider, title);
            slider.ValueChanged += (_, args) =>
            {
                slider.Header = Label(Caption(args.NewValue));
                set(percent ? args.NewValue / 100 : args.NewValue);
                ApplyThemePreview();
            };
            controls.Children.Add(slider);
        }
        Amount("Fensterhintergrund", themePreview.BackgroundOpacity, value => themePreview.BackgroundOpacity = value);
        Amount("Karten und Flächen", themePreview.PanelOpacity, value => themePreview.PanelOpacity = value);
        Amount("Titelleiste und Seitenleisten", themePreview.SidebarOpacity, value => themePreview.SidebarOpacity = value);
        Amount("Eingabefelder", themePreview.ControlOpacity, value => themePreview.ControlOpacity = value);
        Amount("Aktionsbuttons", themePreview.ActionOpacity, value => themePreview.ActionOpacity = value);
        Amount("Popups und Dialoge", themePreview.PopupOpacity, value => themePreview.PopupOpacity = value);
        Amount("Rahmen", themePreview.BorderOpacity, value => themePreview.BorderOpacity = value);
        controls.Children.Add(Label("NATIVES WINDOWS ACRYLIC", 12));
        var acrylic = new ToggleSwitch
        {
            Header = Label("Acrylic verwenden"), IsOn = themePreview.Acrylic,
            IsEnabled = DesktopAcrylicController.IsSupported(), Foreground = white
        };
        acrylic.Toggled += (_, _) => { themePreview.Acrylic = acrylic.IsOn; ApplyThemePreview(); };
        controls.Children.Add(acrylic);
        Amount("Acrylic-Tönung", themePreview.TintOpacity, value => themePreview.TintOpacity = value);
        Amount("Acrylic-Luminosität", themePreview.LuminosityOpacity, value => themePreview.LuminosityOpacity = value);
        controls.Children.Add(Label("Windows bestimmt die Blur-Stärke. Bei inaktivem Fenster, Energiesparmodus oder deaktivierten Windows-Transparenzeffekten kann Acrylic deckend sein. Ohne Acrylic ist der Fensterhintergrund deckend."));
        controls.Children.Add(Label("RUNDUNGEN", 12));
        Amount("Eckenradius", themePreview.Radius, value => themePreview.Radius = value, false);
        var colors = new StackPanel { Spacing = 10 };
        void ColorSetting(string title, Color value, Action<Color> set)
        {
            var swatch = new SolidColorBrush(value);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new Border { Width = 24, Height = 24, Background = swatch, CornerRadius = new CornerRadius(4) });
            var text = Label($"{title}\n#{value.R:X2}{value.G:X2}{value.B:X2}", 12);
            content.Children.Add(text);
            var picker = new ColorPicker
            {
                Color = value, IsAlphaEnabled = false, IsMoreButtonVisible = false,
                IsHexInputVisible = true, IsColorChannelTextInputVisible = true, Foreground = white
            };
            picker.ColorChanged += (_, args) =>
            {
                set(args.NewColor);
                swatch.Color = args.NewColor;
                text.Text = $"{title}\n#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
                ApplyThemePreview();
            };
            var button = new Button
            {
                Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10),
                Flyout = new Flyout { Content = picker }
            };
            ThemeCorners(button);
            AutomationProperties.SetName(button, title + " ändern");
            colors.Children.Add(button);
        }
        ColorSetting("Hintergrundfarbe", themePreview.Background, value => themePreview.Background = value);
        ColorSetting("Flächenfarbe", themePreview.Panel, value => themePreview.Panel = value);
        ColorSetting("Seitenleistenfarbe", themePreview.Sidebar, value => themePreview.Sidebar = value);
        ColorSetting("Dialogfarbe", themePreview.Popup, value => themePreview.Popup = value);
        ColorSetting("Akzentfarbe", themePreview.Accent, value => themePreview.Accent = value);
        ColorSetting("Aktionsfarbe", themePreview.Action, value => themePreview.Action = value);
        ColorSetting("Textfarbe", themePreview.Text, value => themePreview.Text = value);
        ColorSetting("Sekundärer Text", themePreview.Muted, value => themePreview.Muted = value);
        ColorSetting("Text auf Aktionsbuttons", themePreview.OnAction, value => themePreview.OnAction = value);
        ColorSetting("Rahmenfarbe", themePreview.Border, value => themePreview.Border = value);
        ColorSetting("Acrylic-Tönungsfarbe", themePreview.AcrylicTint, value => themePreview.AcrylicTint = value);
        controls.Children.Add(new Expander
        {
            Header = Label("Farben und Hexwerte"), Content = colors,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        controls.Children.Add(Label("Sehr transparente Flächen können den Textkontrast verringern. Die Werkzeugleiste und ihr Reset bleiben bewusst neutral und lesbar. Windows-Kontrastfarben werden nicht überschrieben.", 11));
        var scroll = new ScrollViewer
        {
            Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        ThemePreviewPane.Child = root;
    }

    private static Color Opacity(Color color, double amount) =>
        Color.FromArgb((byte)Math.Round(amount * 255), color.R, color.G, color.B);

    private static Color Mix(Color color, Color target, double amount) => Color.FromArgb(255,
        (byte)Math.Round(color.R + (target.R - color.R) * amount),
        (byte)Math.Round(color.G + (target.G - color.G) * amount),
        (byte)Math.Round(color.B + (target.B - color.B) * amount));
}

[Microsoft.UI.Xaml.Data.Bindable]
internal sealed class ThemePreviewMetrics : INotifyPropertyChanged
{
    private CornerRadius corners = new(10);
    private CornerRadius topCorners = new(10, 10, 0, 0);
    public event PropertyChangedEventHandler? PropertyChanged;
    public CornerRadius Corners
    {
        get => corners;
        set
        {
            if (corners.Equals(value)) return;
            corners = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Corners)));
        }
    }
    public CornerRadius TopCorners
    {
        get => topCorners;
        set
        {
            if (topCorners.Equals(value)) return;
            topCorners = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TopCorners)));
        }
    }
}
