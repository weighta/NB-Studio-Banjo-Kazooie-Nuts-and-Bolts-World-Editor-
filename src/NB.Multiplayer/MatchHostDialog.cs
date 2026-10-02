using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NB.Multiplayer;

/// <summary>
/// "Your edition and mods don't match the host's": shown when joining a room (and when starting the room's game again)
/// while the selected edition is not the host's. Three clear choices: match the host (download / build their mods),
/// join anyway with your own, or cancel.
/// </summary>
public sealed class MatchHostDialog : Window
{
    public enum Choice { Match, JoinAnyway, Cancel }
    public Choice Result { get; private set; } = Choice.Cancel;

    public MatchHostDialog(Window owner, string hostName, string hostEdition, IEnumerable<string> hostMods, string myEdition, IEnumerable<string> myMods)
    {
        Owner = owner; Title = "NB Multiplayer"; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = (Brush)FindResource("Bg");
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22), MaxWidth = 620 };
        root.Children.Add(new TextBlock { Text = "Your mods don't match the host's", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock
        {
            Text = $"The edition and mods you have selected do not match {hostName}'s. Would you like to match the host's mods?",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 16),
        });
        root.Children.Add(Side($"{hostName} (host) plays", hostEdition, hostMods, "Accent"));
        root.Children.Add(Side("You have selected", myEdition, myMods, "Sub"));
        root.Children.Add(new TextBlock
        {
            Text = "Match the host: NB Multiplayer gets the host's mods (downloading any you don't have from the room) and switches you to the same edition. " +
                   "Join anyway: keep yours. Different mods can stop the game from joining, or make your games go out of sync.",
            Style = (Style)FindResource("SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 18),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button B(string text, Choice c, bool primary)
        {
            var b = new Button { Content = text, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(10, 0, 0, 0), FontSize = 13.5, IsDefault = primary, IsCancel = c == Choice.Cancel };
            if (primary) b.Style = (Style)FindResource("Primary"); else b.Style = (Style)FindResource("BaseButton");
            b.Click += (_, _) => { Result = c; DialogResult = c != Choice.Cancel; };
            return b;
        }
        buttons.Children.Add(B("Cancel", Choice.Cancel, false));
        buttons.Children.Add(B("Join anyway", Choice.JoinAnyway, false));
        buttons.Children.Add(B("Match the host's mods", Choice.Match, true));
        root.Children.Add(buttons);
        Content = root;
    }

    UIElement Side(string label, string edition, IEnumerable<string> mods, string color)
    {
        var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 10, 16, 10) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SubText"), FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = edition, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(color == "Sub" ? "Text" : color), Margin = new Thickness(0, 2, 0, 2) });
        var list = mods.ToList();
        sp.Children.Add(new TextBlock { Text = list.Count == 0 ? "No mods (the original game)" : "Mods: " + string.Join(", ", list), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("SubText") });
        card.Child = sp;
        return card;
    }
}
