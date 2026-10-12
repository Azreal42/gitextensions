using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace GitExtensions.Desktop.Controls;

/// <summary>Keep original viewer labels and shortcuts together, without Fluent's fixed 456-pixel flyout ceiling.</summary>
public static class DiffViewerPopupLayout
{
    public static void Configure(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        menu.Resources["FlyoutThemeMaxWidth"] = double.PositiveInfinity;
        menu.MaxWidth = double.PositiveInfinity;
    }

    public static void Configure(MenuFlyout flyout)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        // MenuFlyout is not a resource host. Its real presenter is created by
        // Avalonia when opening; apply the same local resource to that surface.
        flyout.Opened += (_, _) =>
        {
            MenuFlyoutPresenter? presenter = flyout.Items.OfType<MenuItem>()
                .SelectMany(item => item.GetVisualAncestors().OfType<MenuFlyoutPresenter>()).FirstOrDefault();
            if (presenter is not null)
            {
                presenter.Resources["FlyoutThemeMaxWidth"] = double.PositiveInfinity;
                presenter.MaxWidth = double.PositiveInfinity;
            }
        };
    }
}
