using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReExtractor.Core;

namespace ReExtractor.Gui;

public sealed class MaterialSelectionWindow : Window
{
    public MaterialSelectionWindow(MaterialResolution resolution)
    {
        Title = "选择材质版本";
        Width = 760;
        Height = 380;
        MinWidth = 560;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var description = new TextBlock
        {
            Text = $"{Path.GetFileName(resolution.MeshPath)}\n找到多个材质名全部匹配的版本，请选择本次使用的材质。预览和导出将使用同一选择。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        var choices = new ListBox
        {
            Name = "MaterialChoices",
            ItemsSource = resolution.Candidates.Select(c => $"{c.Path}  （{c.MatchedNames}/{c.RequiredNames}）").ToArray(),
            SelectedIndex = -1
        };
        Grid.SetRow(choices, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(buttons, 2);
        var cancel = new Button { Name = "CancelMaterial", Content = "取消" };
        var apply = new Button { Name = "ApplyMaterial", Content = "使用所选材质", IsEnabled = false };
        cancel.Click += (_, _) => Close(null);
        choices.SelectionChanged += (_, _) => apply.IsEnabled = choices.SelectedIndex >= 0;
        apply.Click += (_, _) => { if (choices.SelectedIndex >= 0) Close(resolution.Candidates[choices.SelectedIndex].Path); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        panel.Children.Add(description);
        panel.Children.Add(choices);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
