using System.Windows;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

internal static class AssistantListStyle
{
    public static Style Create()
    {
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4))); style.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7)); border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch); border.AppendChild(presenter); style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SettingsAccentSoftBrush"))); style.Triggers.Add(selected);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("SettingsNavHoverBrush"))); style.Triggers.Add(hover);
        return style;
    }
}
