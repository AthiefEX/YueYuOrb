using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class ThemeResourceTests
{
    [Fact]
    public void PrimaryButtonHoverBackgroundIsBrush()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = LoadApplicationResources();
                var style = Assert.IsType<Style>(
                    resources["PrimaryButtonStyle"]);
                var trigger = Assert.Single(
                    style.Triggers.OfType<Trigger>(),
                    candidate =>
                        candidate.Property == UIElement.IsMouseOverProperty &&
                        Equals(candidate.Value, true));
                var setter = Assert.Single(
                    trigger.Setters.OfType<Setter>(),
                    candidate =>
                        candidate.Property == Control.BackgroundProperty);

                Assert.IsAssignableFrom<Brush>(setter.Value);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "STA test thread timed out.");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        FileInfo? appXaml = null;
        while (directory is not null)
        {
            var candidate = new FileInfo(Path.Combine(
                directory.FullName,
                "src",
                "FloatDesk.App",
                "App.xaml"));
            if (candidate.Exists)
            {
                appXaml = candidate;
                break;
            }
            directory = directory.Parent;
        }
        Assert.NotNull(appXaml);

        var document = XDocument.Load(appXaml.FullName);
        var resources = document.Root?.Elements().Single(
            element => element.Name.LocalName == "Application.Resources");
        Assert.NotNull(resources);
        var innerXaml = string.Concat(
            resources.Nodes().Select(
                node => node.ToString(SaveOptions.DisableFormatting)));
        var resourceDictionaryXaml =
            "<ResourceDictionary " +
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            innerXaml +
            "</ResourceDictionary>";
        return Assert.IsType<ResourceDictionary>(
            XamlReader.Parse(resourceDictionaryXaml));
    }
}
