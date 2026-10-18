using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;

namespace GitExtensions.Linux.Desktop.Controls;

internal static class ClassicCompletionList
{
    internal static void Apply(ListBox list)
    {
        // The Windows ListBox highlights its selection; hovering another row does not select it.
        foreach (bool selected in new[] { false, true })
        {
            Selector Row(Selector? selector) => selected
                ? selector.OfType<ListBoxItem>().Class(":selected")
                : selector.OfType<ListBoxItem>().Not(item => item.Class(":selected"));
            string background = selected ? "ClassicHighlightBrush" : "ClassicWindowBrush";
            string foreground = selected ? "ClassicHighlightTextBrush" : "ClassicTextBrush";
            list.Styles.Add(new Style(Row)
            { Setters = { new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension(background)),
                new Setter(TemplatedControl.ForegroundProperty, new DynamicResourceExtension(foreground)) } });
            list.Styles.Add(new Style(selector => Row(selector).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension(background)),
                new Setter(ContentPresenter.ForegroundProperty, new DynamicResourceExtension(foreground)) } });
        }
    }
}
