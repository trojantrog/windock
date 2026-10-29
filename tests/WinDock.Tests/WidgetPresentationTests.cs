using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using WinDock.Models;
using WinDock.ViewModels;
using WinDock.Views;

namespace WinDock.Tests;

public class WidgetPresentationTests
{
    [Fact]
    public void Activity_keeps_both_numbers_and_progress_indicators()
    {
        var widget = new ActivityWidget("activity", new WidgetSpec { Kind = WidgetSpec.Activity })
            { CpuText = "37%", RamText = "64%", Cpu = .37, Ram = .64 };
        widget.RefreshFace();
        var face = Assert.IsType<WidgetPresentation>(widget.Presentation);
        Assert.Equal(("37%", "CPU", "64%", "RAM"), (face.Value, face.Caption, face.SecondValue, face.SecondCaption));
        Assert.Equal((.37, .64), (face.Progress, face.SecondProgress));
        Assert.True(face.Dual);
    }

    [Fact]
    public void Numeric_updates_keep_the_same_reserved_width_and_both_network_directions()
    {
        var widget = new NetworkWidget("network", new WidgetSpec { Kind = WidgetSpec.Network }) { Down = "0 B/s", Up = "0 B/s" };
        widget.RefreshFace();
        var width = widget.Presentation!.MinWidth;
        widget.Down = "1023 GB/s";
        widget.Up = "12.5 MB/s";
        widget.RefreshFace();
        Assert.Equal(width, widget.Presentation!.MinWidth);
        Assert.Equal(("1023", "GB/s", "12.5", "MB/s"),
            (widget.Presentation.Value, widget.Presentation.Unit, widget.Presentation.SecondValue, widget.Presentation.SecondUnit));
        Assert.Equal("", widget.Presentation.Caption);
        Assert.Null(widget.Presentation.SecondCaption);
        Assert.True(widget.Presentation.Stacked);
        var labels = widget.Presentation.Caption + widget.Presentation.SecondCaption + widget.Face!.Caption + widget.Face.Tip;
        Assert.DoesNotContain("↑", labels);
        Assert.DoesNotContain("↓", labels);
    }

    [Fact]
    public void New_widgets_keep_their_value_units_state_and_tooltip()
    {
        var widget = new SampleWidget(new WidgetSpec { Kind = "token-speed" },
            new CompactFace("52.7", "Token/s · 最近", Tip: "最近输出速度 · gpt-6", Dim: true));
        widget.RefreshFace();
        Assert.Equal("52.7", widget.Presentation!.Value);
        Assert.Equal("Token/s · 最近", widget.Presentation.Caption);
        Assert.Equal(widget.Face!.Tip, widget.Presentation.Tip);
        Assert.False(widget.Presentation.HasMarker);
        Assert.True(widget.Presentation.Dim);
    }

    [Theory]
    [InlineData("token-speed", null, true, false)]
    [InlineData("token-speed", "square", true, true)]
    [InlineData("token-speed", "square", false, true)]
    [InlineData("token-speed", "wide", true, false)]
    [InlineData("clock", null, true, true)]
    [InlineData("clock", null, false, false)]
    [InlineData("clock", "wide", true, false)]
    public void Token_speed_defaults_to_readable_and_explicit_sizes_remain_authoritative(string kind, string? size, bool compact, bool expected)
    {
        var widget = new SampleWidget(new WidgetSpec { Kind = kind, Size = size }, new CompactFace("12.3"));
        Assert.Equal(expected, widget.Square(compact));
    }

    [Fact]
    public void The_reserved_height_matches_the_selected_widget_shape()
    {
        var token = new SampleWidget(new WidgetSpec { Kind = "token-speed" }, new CompactFace("12.3"));
        Assert.Equal(64, WidgetPresentation.ContentHeight([token], 30, true));
        token.Spec.Size = WidgetSpec.SquareSize;
        Assert.Equal(30, WidgetPresentation.ContentHeight([token], 30, true));
        Assert.Equal(60, WidgetPresentation.ContentHeight([token], 60, true));
        Assert.Equal(30, WidgetPresentation.ContentHeight([], 30, true));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(18)]
    public void Wpf_keeps_numeric_type_legible_and_inside_the_plate_when_app_icons_shrink(double iconSize) => InSta(() =>
    {
        var resources = ReadableResources(iconSize);
        var activity = new ActivityWidget("activity", new WidgetSpec { Kind = WidgetSpec.Activity, Size = WidgetSpec.WideSize })
            { CpuText = "100%", RamText = "100%", Cpu = 1, Ram = 1 };
        var network = new NetworkWidget("network", new WidgetSpec { Kind = WidgetSpec.Network, Size = WidgetSpec.WideSize })
            { Down = "1023 GB/s", Up = "12.5 MB/s" };
        var token = new SampleWidget(new WidgetSpec { Kind = "token-speed" }, new CompactFace("52.7", "Token/s · 最近"));
        var panel = new LensPanel { Resources = resources, Inset = new Thickness(8), Gap = 11, Mode = LensMode.None };
        var selector = new FaceSelector { Compact = new DataTemplate(), Readable = (DataTemplate)resources["ReadableWidget"], On = true };
        foreach (var tile in new WidgetTile[] { activity, network, token })
        {
            tile.RefreshFace();
            var presenter = new ContentPresenter { Content = tile, ContentTemplate = selector.SelectTemplate(tile, panel) };
            LensPanel.SetLens(presenter, false);
            panel.Children.Add(presenter);
        }
        panel.Measure(new Size(1000, 170));
        panel.Arrange(new Rect(0, 0, 1000, 170));
        panel.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        panel.Measure(new Size(1000, 170));
        panel.Arrange(new Rect(0, 0, 1000, 170));
        panel.UpdateLayout();
        Assert.Equal(64 + 16, panel.Bar.Height);
        var texts = Descendants(panel).OfType<TextBlock>().Where(t => t.ActualHeight > 0).ToArray();
        foreach (var value in new[] { "100%", "1023 GB/s", "12.5 MB/s", "52.7" })
        {
            Assert.True(texts.Any(t => Shown(t) == value), string.Join(" | ", texts.Select(Shown)));
            var text = texts.First(t => Shown(t) == value);
            Assert.Equal(22, text.FontSize * text.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, 1, 1)).Height);
            var natural = new FormattedText(Shown(text), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground, 1);
            Assert.True(natural.WidthIncludingTrailingWhitespace <= text.ActualWidth + .5, value);
        }
        foreach (var text in texts)
        {
            var bounds = text.TransformToAncestor(panel).TransformBounds(new Rect(text.RenderSize));
            Assert.True(panel.Bar.Contains(bounds), Shown(text));
        }
        Assert.Contains(texts, t => Shown(t) == "Token/s · 最近" && t.FontSize == 13);
        Assert.DoesNotContain(texts, t => Shown(t).Contains("下载") || Shown(t).Contains("上传") || Shown(t).Contains('↑') || Shown(t).Contains('↓'));
        var originalWidth = panel.BaseWidth;
        network.Down = "0 B/s";
        network.Up = "0 B/s";
        network.RefreshFace();
        panel.Measure(new Size(1000, 170));
        Assert.Equal(originalWidth, panel.BaseWidth);
    });

    static string Shown(TextBlock text) => new TextRange(text.ContentStart, text.ContentEnd).Text;

    static ResourceDictionary ReadableResources(double iconSize)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "WinDock", "Views", "DockWindow.xaml"))) directory = directory.Parent;
        Assert.NotNull(directory);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new HashSet<string> { "TileRoot", "WidgetRoot", "WidgetFace", "WidgetValue", "WidgetCaption", "ReadableWidget" };
        var source = XDocument.Load(Path.Combine(directory.FullName, "src", "WinDock", "Views", "DockWindow.xaml"));
        var xml = new XElement(p + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
            source.Root!.Element(p + "Window.Resources")!.Elements().Where(e => keys.Contains(e.Attribute(x + "Key")?.Value ?? "")));
        xml.AddFirst(new XElement(p + "FontFamily", new XAttribute(x + "Key", "DockFont"), "Segoe UI Variable Text, Segoe UI"),
            new XElement(p + "FontFamily", new XAttribute(x + "Key", "Glyphs"), "Segoe Fluent Icons"),
            new XElement(p + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "Show")));
        foreach (var setter in xml.Descendants(p + "EventSetter").ToArray()) setter.Remove();
        foreach (var setter in xml.Descendants(p + "Setter").Where(e => e.Attribute("TargetName") is not null))
        {
            var name = setter.Attribute("TargetName")!.Value;
            var element = xml.Descendants().Single(e => e.Attribute(x + "Name")?.Value == name);
            setter.SetAttributeValue("Property", element.Name.LocalName + "." + setter.Attribute("Property")!.Value);
        }
        var parser = new ParserContext();
        parser.XmlnsDictionary[""] = p.NamespaceName;
        parser.XmlnsDictionary["x"] = x.NamespaceName;
        var root = (ResourceDictionary)XamlReader.Parse(xml.ToString(SaveOptions.DisableFormatting), parser);
        root["IconSize"] = iconSize;
        root["WidgetHeight"] = WidgetPresentation.Height(iconSize);
        root["WideSize"] = iconSize * 2 + 11;
        foreach (var key in new[] { "DockInk", "DockMuted", "DockLine", "WidgetSurface", "WidgetBorder" }) root[key] = Brushes.Black;
        return root;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    static void InSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    sealed class SampleWidget(WidgetSpec spec, CompactFace compact) : WidgetTile("sample", spec)
    {
        protected override CompactFace Compact() => compact;
        public override void Tick(DateTime now) { }
    }
}
