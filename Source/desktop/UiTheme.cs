using System;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopAccounts {
    public sealed class DialogShell {
        readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
        public Grid View;
        public StackPanel Body, Footer;
        public TextBlock Error;
        public bool IsBusy;
        public Task<bool> Completion { get { return completion.Task; } }
        public void Complete(bool accepted) { if (!IsBusy) completion.TrySetResult(accepted); }
        public void ShowError(string message) { Error.Text = message; Error.Visibility = String.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; }
    }
    public static class UiTheme {
        public static System.Drawing.Icon TrayIcon() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Switcher.ico"))
            using (var icon = new System.Drawing.Icon(stream, 32, 32)) return (System.Drawing.Icon)icon.Clone();
        }
        public static ImageSource WindowIcon() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Switcher.ico")) {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderBy(image => Math.Abs(image.PixelWidth - 32)).First(); frame.Freeze(); return frame;
            }
        }
        public static ResourceDictionary Resources() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml")) return (ResourceDictionary)XamlReader.Load(stream);
        }
        public static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        public static TextBlock Text(string text, double size, string color) { return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap }; }
        public static Button Button(FrameworkElement scope, string text, string style) { return new Button { Content = text, Style = (Style)scope.FindResource((object)style ?? typeof(Button)), MinWidth = 66 }; }
        public static Border Card(UIElement content) { return new Border { Background = Brush("#F8FAFD"), BorderBrush = Brush("#E8ECF3"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = content }; }
        public static System.Windows.Shapes.Path CloseGlyph() { return new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 8,8 M 0,8 L 8,0"), Width = 8, Height = 8, Stroke = Brush("#65738A"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
        public static DialogShell Page(string title, string subtitle, string glyph, UiText text = null) {
            if (text == null) text = new UiText("en");
            var shell = new DialogShell();
            var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Resources.MergedDictionaries.Add(Resources()); shell.View = root;
            var header = new Grid { Margin = new Thickness(4, 6, 2, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            var back = Button(root, "‹", "IconButton"); back.MinWidth = 0; back.Width = 27; back.FontSize = 23; back.Padding = new Thickness(0); back.ToolTip = text["back"]; back.HorizontalAlignment = HorizontalAlignment.Left; back.VerticalAlignment = VerticalAlignment.Top; back.Click += delegate { shell.Complete(false); }; header.Children.Add(back);
            var titles = new StackPanel(); Grid.SetColumn(titles, 1); var heading = Text(title, 14, "#263142"); heading.FontWeight = FontWeights.SemiBold; titles.Children.Add(heading);
            if (!String.IsNullOrEmpty(subtitle)) { var hint = Text(subtitle, 11, "#8A95A7"); hint.Margin = new Thickness(0, 4, 0, 0); titles.Children.Add(hint); } header.Children.Add(titles);
            var close = Button(root, "", "CloseButton"); close.Name = "PageClose"; close.MinWidth = 0; close.Content = CloseGlyph(); close.ToolTip = text["back"]; System.Windows.Automation.AutomationProperties.SetName(close, text["back"]); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(close, 2); close.Click += delegate { shell.Complete(false); }; header.Children.Add(close); root.Children.Add(header);
            shell.Body = new StackPanel { Margin = new Thickness(4, 0, 4, 2) };
            var bodyScroll = new ScrollViewer { Style = (Style)root.FindResource("AppScrollViewer"), Focusable = false, FocusVisualStyle = null, Content = shell.Body, MaxHeight = Math.Max(80, Math.Min(300, SystemParameters.WorkArea.Height - 190)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(bodyScroll, 1); root.Children.Add(bodyScroll);
            var bottom = new StackPanel { Margin = new Thickness(4, 12, 4, 4) }; shell.Error = Text("", 11, "#BE6461"); shell.Error.Visibility = Visibility.Collapsed; shell.Error.Margin = new Thickness(0, 0, 0, 10); bottom.Children.Add(shell.Error);
            bottom.Children.Add(new Border { Height = 1, Background = Brush("#EDF0F5"), Margin = new Thickness(0, 0, 0, 12) }); shell.Footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; bottom.Children.Add(shell.Footer); Grid.SetRow(bottom, 2); root.Children.Add(bottom);
            return shell;
        }
    }
}
