using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Views;

public sealed class SearchWindow : DesktopToolWindow
{
    readonly DesktopSearch search;
    readonly TextBox query;
    readonly ListBox results;
    CancellationTokenSource? pending;
    int version;

    public SearchWindow(Settings settings, DesktopSearch search) : base(settings, Loc.T("searchTitle"), 780, 620)
    {
        this.search = search;
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition());
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var locations = ActionButton("searchLocations", OpenLocations);
        DockPanel.SetDock(locations, Dock.Right);
        heading.Children.Add(locations);
        heading.Children.Add(Text(Loc.T("searchTitle"), 24));
        Layout.Children.Add(heading);
        query = Input(Loc.T("searchHint"));
        query.FontSize = 20;
        query.Margin = new Thickness(0, 0, 0, 14);
        System.Windows.Automation.AutomationProperties.SetName(query, Loc.T("searchHint"));
        Grid.SetRow(query, 1); Layout.Children.Add(query);
        results = List();
        System.Windows.Automation.AutomationProperties.SetName(results, Loc.T("searchResults"));
        Grid.SetRow(results, 2); Layout.Children.Add(results);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var reveal = ActionButton("showInExplorer", Reveal);
        DockPanel.SetDock(reveal, Dock.Right); footer.Children.Add(reveal); footer.Children.Add(Status);
        Grid.SetRow(footer, 3); Layout.Children.Add(footer);
        Status.Text = Loc.T("searchHint");
        query.TextChanged += async (_, _) => await SearchAsync();
        query.PreviewKeyDown += OnQueryKey;
        results.MouseDoubleClick += (_, _) => OpenSelection();
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelection(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) =>
        {
            query.Focus();
            try { await search.WarmAsync(); } catch (Exception) { Status.Text = Loc.T("searchAppsUnavailable"); }
        };
        Closed += (_, _) => { pending?.Cancel(); pending?.Dispose(); pending = null; };
    }

    public void FocusSearch() { Activate(); query.Focus(); query.SelectAll(); }

    async Task SearchAsync()
    {
        var current = ++version;
        pending?.Cancel(); pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        var token = pending.Token;
        var text = query.Text;
        if (string.IsNullOrWhiteSpace(text)) { results.Items.Clear(); Status.Text = Loc.T("searchHint"); return; }
        Status.Text = Loc.T("searching");
        try
        {
            await Task.Delay(180, token);
            var batch = await search.SearchAsync(text, Settings.Desktop.EverythingInstance, token);
            if (current != version || token.IsCancellationRequested) return;
            results.Items.Clear();
            foreach (var hit in batch.Hits) results.Items.Add(Row(hit, hit.Name, hit.Kind == "app" ? Loc.T("catPrograms") + " · " + hit.Detail : hit.Detail));
            if (results.Items.Count > 0) results.SelectedIndex = 0;
            Status.Text = (batch.EverythingAvailable ? "Everything · " : "") + Loc.F("searchCount", batch.Hits.Count)
                + (batch.Error is not null ? " · " + batch.Error : "")
                + (batch.Limited ? " · " + Loc.T("searchLimited") : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (current == version) Status.Text = Loc.F("failed", e.Message);
        }
    }

    void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OpenSelection(); }
        else if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (results.Items.Count > 0)
            {
                results.SelectedIndex = Math.Clamp(results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, results.Items.Count - 1);
                results.ScrollIntoView(results.SelectedItem);
            }
        }
    }

    SearchHit? Selected => (results.SelectedItem as ListBoxItem)?.Tag as SearchHit;
    void OpenSelection()
    {
        if (Selected is not { } hit) return;
        if (hit.Open()) Close();
        else Status.Text = Loc.F("launchFailed", hit.Name);
    }
    void Reveal()
    {
        if (Selected is not { } hit) return;
        var path = hit.Application is { } app ? Launcher.RevealTarget(app) : hit.Path;
        if (path is not null) Launcher.Reveal(path);
    }
    void OpenLocations()
    {
        new SearchLocationsWindow(Settings) { Owner = this }.ShowDialog();
        _ = SearchAsync();
    }
}

sealed class SearchLocationsWindow : DesktopToolWindow
{
    readonly TextBox instance;
    readonly ComboBox shortcut;

    public SearchLocationsWindow(Settings settings) : base(settings, Loc.T("searchLocations"), 720, 480)
    {
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition());
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var note = Text(Loc.T("searchScopeHint"), 13, true); note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 0, 0, 12); Layout.Children.Add(note);
        var details = new StackPanel();
        details.Children.Add(Text(Loc.T("everythingInstance")));
        instance = Input(Loc.T("everythingInstance")); instance.Text = settings.Desktop.EverythingInstance;
        instance.Margin = new Thickness(0, 10, 0, 10); details.Children.Add(instance);
        details.Children.Add(Text(Loc.T("everythingInstanceHint"), 12, true));
        Grid.SetRow(details, 1); Layout.Children.Add(details);
        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        controls.Children.Add(Text(Loc.T("searchShortcut")));
        shortcut = new ComboBox { ItemsSource = new[] { "Alt+Space", "Ctrl+Shift+Space", "Win+Shift+Space" }, SelectedItem = settings.Desktop.SearchHotkey, MinWidth = 160, Margin = new Thickness(10, 0, 0, 6), Padding = new Thickness(6) };
        controls.Children.Add(shortcut);
        Grid.SetRow(controls, 2); Layout.Children.Add(controls);
        var bottom = new DockPanel();
        var save = ActionButton("save", Save); DockPanel.SetDock(save, Dock.Right); bottom.Children.Add(save); bottom.Children.Add(Status);
        Grid.SetRow(bottom, 3); Layout.Children.Add(bottom);
    }

    void Save()
    {
        Settings.Desktop.EverythingInstance = instance.Text.Trim();
        Settings.Desktop.SearchHotkey = shortcut.SelectedItem as string ?? "Alt+Space";
        Settings.Save();
        DesktopHub.NotifyShortcutChanged();
        Close();
    }
}
