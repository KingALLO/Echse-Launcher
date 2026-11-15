using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Echse.App;

public sealed class NeutralAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? controller;
    private Color tint = Color.FromArgb(255, 32, 32, 32);
    private Color fallback = Color.FromArgb(255, 28, 28, 28);
    private float tintOpacity = 0.12f;
    private float luminosityOpacity = 0.08f;

    public void Update(Color tintColor, double tintAmount, double luminosityAmount, Color fallbackColor)
    {
        tint = tintColor;
        fallback = fallbackColor;
        tintOpacity = (float)tintAmount;
        luminosityOpacity = (float)luminosityAmount;
        ApplySettings();
    }

    private void ApplySettings()
    {
        if (controller is null) return;
        controller.TintColor = tint;
        controller.TintOpacity = tintOpacity;
        controller.LuminosityOpacity = luminosityOpacity;
        controller.FallbackColor = fallback;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        controller = new DesktopAcrylicController();
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
        ApplySettings();
        controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose();
        controller = null;
        base.OnTargetDisconnected(target);
    }
}
