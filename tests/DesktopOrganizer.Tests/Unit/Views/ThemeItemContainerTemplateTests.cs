using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopOrganizer.Tests.Helpers;

namespace DesktopOrganizer.Tests.Unit.Views;

/// <summary>
/// Regression coverage for three Settings-window rendering defects the user reported on 2026-10-09, all
/// of them theme-template bugs in <c>Resources/Styles/Theme.Controls.xaml</c> rather than binding bugs
/// in the sections that showed them:
/// <list type="bullet">
/// <item>the General section's language <see cref="ComboBox"/> (DisplayMemberPath) showed
/// "DesktopOrganizer.UI.Models.LanguageOption" in its closed box — the template only forwarded
/// <c>SelectionBoxItemTemplate</c>, never <c>ItemTemplateSelector</c>, which is how DisplayMemberPath is
/// delivered;</item>
/// <item>the Backup section's snapshot <see cref="ListView"/> rows showed
/// "DesktopOrganizer.Core.LayoutSnapshot" — <c>Theme.ListView</c> reused the ListBox container, whose
/// <see cref="ContentPresenter"/> ignores <see cref="GridView"/> cell templates;</item>
/// <item>the Panels section's selected row was invisible in Light — <c>Theme.Selection.Fill</c> alone is
/// white on white, so <c>Theme.ListBoxItem</c> now also paints an accent indicator bar.</item>
/// </list>
/// Real templated controls, measured in an off-screen window, the same way
/// <see cref="SettingsWindowLayoutWidthTests"/> works.
/// </summary>
[Trait(UnitTests, "DesktopOrganizer.UI")]
public class ThemeItemContainerTemplateTests
{
    /// <summary>A ComboBox using DisplayMemberPath renders the member's text in its CLOSED box, not ToString().</summary>
    [Fact]
    public void ThemeComboBox_DisplayMemberPath_ClosedBoxShowsMemberText()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var combo = new ComboBox
            {
                Style = (Style)app.Resources["Theme.ComboBox"],
                DisplayMemberPath = nameof(Row.Name),
                ItemsSource = new[] { new Row("English"), new Row("Русский") },
                SelectedIndex = 0,
            };

            using var host = OffscreenWindow.Show(combo);

            var contentSite = Assert.IsType<ContentPresenter>(combo.Template.FindName("ContentSite", combo));
            Assert.NotNull(contentSite.ContentTemplateSelector);

            var text = Assert.Single(VisualDescendants<TextBlock>(contentSite));
            Assert.Equal("English", text.Text);
        });
    }

    /// <summary>A ComboBox with an explicit ItemTemplate (the page picker, the language picker) keeps working in the closed box.</summary>
    [Fact]
    public void ThemeComboBox_ItemTemplate_ClosedBoxShowsTemplatedText()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var template = new DataTemplate();
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Name)));
            template.VisualTree = textFactory;

            var combo = new ComboBox
            {
                Style = (Style)app.Resources["Theme.ComboBox"],
                ItemTemplate = template,
                ItemsSource = new[] { new Row("Default") },
                SelectedIndex = 0,
            };

            using var host = OffscreenWindow.Show(combo);

            var contentSite = Assert.IsType<ContentPresenter>(combo.Template.FindName("ContentSite", combo));
            var text = Assert.Single(VisualDescendants<TextBlock>(contentSite));
            Assert.Equal("Default", text.Text);
        });
    }

    /// <summary>A Theme.ListView row with a GridView renders the column cell templates, not the item's ToString().</summary>
    [Fact]
    public void ThemeListView_GridViewRow_RendersCellTemplates()
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var cellTemplate = new DataTemplate();
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Name)));
            cellTemplate.VisualTree = textFactory;

            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Name", Width = 120, CellTemplate = cellTemplate });

            var listView = new ListView
            {
                Style = (Style)app.Resources["Theme.ListView"],
                View = gridView,
                ItemsSource = new[] { new Row("Alpha") },
            };

            using var host = OffscreenWindow.Show(listView);

            var row = Assert.IsType<ListViewItem>(listView.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Single(VisualDescendants<GridViewRowPresenter>(row));

            var texts = VisualDescendants<TextBlock>(row).Select(t => t.Text).ToList();
            Assert.Contains("Alpha", texts);
            Assert.DoesNotContain(typeof(Row).FullName, texts);
        });
    }

    /// <summary>A selected Theme.ListBoxItem shows the accent indicator bar; an unselected one does not.</summary>
    /// <param name="selected">Whether the row is selected.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThemeListBoxItem_Selected_ShowsAccentIndicator(bool selected)
    {
        StaTestRunner.Run(() =>
        {
            var app = WpfApplicationHost.EnsureApplication();
            var listBox = new ListBox
            {
                Style = (Style)app.Resources["Theme.ListBox"],
                ItemsSource = new[] { new Row("Games") },
            };

            using var host = OffscreenWindow.Show(listBox);

            var row = Assert.IsType<ListBoxItem>(listBox.ItemContainerGenerator.ContainerFromIndex(0));
            row.IsSelected = selected;
            host.Window.UpdateLayout();

            var indicator = Assert.IsType<Border>(row.Template.FindName("Indicator", row));
            var transparent = indicator.Background is SolidColorBrush { Color.A: 0 };
            Assert.Equal(!selected, transparent);
        });
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }

    private sealed record Row(string Name);

    /// <summary>A hidden off-screen window hosting the control under test so templates apply and layout runs; disposing closes it.</summary>
    private sealed class OffscreenWindow : IDisposable
    {
        private OffscreenWindow(Window window) => Window = window;

        public Window Window { get; }

        /// <summary>Shows <paramref name="content"/> in a new off-screen window and runs a layout pass.</summary>
        /// <param name="content">The control under test.</param>
        /// <returns>A handle whose disposal closes the window.</returns>
        public static OffscreenWindow Show(UIElement content)
        {
            var window = new Window { Content = content, ShowActivated = false, Left = -5000, Top = -5000, Width = 300, Height = 120 };
            window.Show();
            window.UpdateLayout();
            return new OffscreenWindow(window);
        }

        public void Dispose() => Window.Close();
    }
}
