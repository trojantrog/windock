using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Views;

public sealed class SearchWindow : DesktopToolWindow
{
    readonly DesktopSearch search;
    readonly TextBox query;
    readonly ListBox results;
    readonly StackPanel empty;
    readonly Button reveal, scope;
    readonly List<Button> filters = [];
    IReadOnlyList<SearchHit> hits = [];
    SearchCategory filter;
    string? scopeFolder;
    string scopeName = Loc.T("searchScopeAll");
    CancellationTokenSource? pending;
    int version;

    public SearchWindow(Settings settings, DesktopSearch search) : base(settings, Loc.T("searchTitle"), 820, 600)
    {
        this.search = search;
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition());
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        scope = ActionButton("searchScopeAll", OpenScope);
        scope.Background = Brushes.Transparent; scope.BorderThickness = new Thickness(0);
        scope.MaxWidth = 280; scope.VerticalAlignment = VerticalAlignment.Center;
        AddHeaderAction(scope);
        AddHeaderAction(IconButton("\uE713", "searchLocations", OpenLocations));
        var searchBar = new Grid { Margin = new Thickness(0, 4, 0, 18) };
        searchBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        searchBar.ColumnDefinitions.Add(new ColumnDefinition());
        searchBar.Children.Add(Glyph("\uE721", 22));
        query = Input(Loc.T("searchPlaceholder"));
        query.FontSize = 24; query.MinHeight = 52;
        query.BorderThickness = new Thickness(0); query.Background = Brushes.Transparent;
        query.Padding = new Thickness(12, 8, 0, 8);
        System.Windows.Automation.AutomationProperties.SetName(query, Loc.T("searchHint"));
        Grid.SetColumn(query, 1); searchBar.Children.Add(query); Layout.Children.Add(searchBar);
        var categories = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (kind, key) in new[] { (SearchCategory.All, "searchAll"), (SearchCategory.Applications, "catPrograms"),
            (SearchCategory.Documents, "searchDocuments"), (SearchCategory.Folders, "searchFolders"),
            (SearchCategory.Images, "searchImages"), (SearchCategory.Video, "searchVideo"), (SearchCategory.Audio, "searchAudio") })
        {
            var button = ActionButton(key, () => { filter = kind; UpdateScopeLabel(); _ = SearchAsync(); });
            button.Tag = kind; button.BorderThickness = new Thickness(0); filters.Add(button); categories.Children.Add(button);
        }
        Grid.SetRow(categories, 1); Layout.Children.Add(categories);
        results = List();
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(results, Loc.T("searchResults"));
        var body = new Grid(); body.Children.Add(results);
        empty = EmptyState("\uE721", "searchEmptyTitle", "searchEmptyHint"); body.Children.Add(empty);
        Grid.SetRow(body, 2); Layout.Children.Add(body);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        reveal = IconButton("\uE8B7", "showInExplorer", Reveal); reveal.IsEnabled = false;
        var keys = Text(Loc.T("searchKeys"), 11, true); keys.Margin = new Thickness(12, 0, 12, 0);
        DockPanel.SetDock(keys, Dock.Right); footer.Children.Add(keys);
        DockPanel.SetDock(reveal, Dock.Right); footer.Children.Add(reveal); footer.Children.Add(Status);
        Grid.SetRow(footer, 3); Layout.Children.Add(footer);
        Status.Text = "Everything";
        query.TextChanged += async (_, _) => await SearchAsync();
        query.PreviewKeyDown += OnQueryKey;
        results.MouseDoubleClick += (_, _) => OpenSelection();
        results.SelectionChanged += (_, _) =>
        {
            var app = Selected?.Application;
            reveal.IsEnabled = Selected is not null && (app is null || !app.IsRaw && !app.IsLauncher && app.ResolvedTarget is not null);
        };
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelection(); } };
        Loaded += async (_, _) =>
        {
            query.Focus();
            try { await search.WarmAsync(); }
            catch (Exception) { if (IsVisible && string.IsNullOrWhiteSpace(query.Text)) Status.Text = Loc.T("searchAppsUnavailable"); }
        };
        Closed += (_, _) => { pending?.Cancel(); pending?.Dispose(); pending = null; };
        RenderResults();
        UpdateScopeLabel();
    }

    public void FocusSearch() { Activate(); query.Focus(); query.SelectAll(); }

    async Task SearchAsync()
    {
        var current = ++version;
        pending?.Cancel(); pending?.Dispose();
        pending = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken);
        var token = pending.Token;
        var text = query.Text;
        if (string.IsNullOrWhiteSpace(text)) { hits = []; RenderResults(); Status.Text = "Everything"; return; }
        hits = []; RenderResults();
        empty.Visibility = Visibility.Collapsed;
        Status.Text = Loc.T("searching");
        try
        {
            await Task.Delay(180, token);
            var batch = await search.SearchAsync(text, Settings.Desktop.EverythingInstance, token,
                new SearchFilter(filter, scopeFolder), partial =>
                {
                    if (current == version && !token.IsCancellationRequested) ShowBatch(partial, true);
                });
            if (current != version || token.IsCancellationRequested) return;
            ShowBatch(batch, false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (current == version && !token.IsCancellationRequested)
            {
                hits = [];
                RenderResults();
                Status.Text = Loc.F("failed", e.Message);
            }
        }
    }

    void ShowBatch(SearchBatch batch, bool partial)
    {
        hits = batch.Hits; RenderResults();
        if (partial && hits.Count == 0) empty.Visibility = Visibility.Collapsed;
        Status.Text = (filter == SearchCategory.Applications ? Loc.T("catPrograms") + " · " : batch.EverythingAvailable ? "Everything · " : "") + Loc.F("searchCount", batch.Hits.Count)
            + (partial ? " · " + Loc.T("searching") : "")
            + (batch.Error is not null ? " · " + batch.Error : "")
            + (batch.Limited ? " · " + Loc.T(filter == SearchCategory.Applications ? "searchAppsLimited" : "searchLimited") : "");
    }

    void RenderResults()
    {
        var previous = Selected;
        results.Items.Clear();
        foreach (var button in filters)
        {
            var selected = (SearchCategory)button.Tag == filter;
            button.Background = selected ? (Brush)FindResource("GlassSelected") : Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetHelpText(button, selected ? Loc.T("searchFilterSelected") : "");
        }
        foreach (var hit in hits)
        {
            var visual = SearchVisual.For(hit);
            var row = Row(hit, hit.Name, hit.Detail, visual.Glyph);
            var grid = (Grid)row.Content;
            ((TextBlock)grid.Children[0]).Foreground = SystemParameters.HighContrast ? Ink : Paint(visual.Color);
            if (hit.Application is { } pin && ShellIcons.ForPin(pin, 48) is { } icon)
            {
                grid.Children[0].Visibility = Visibility.Collapsed;
                var image = new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality); grid.Children.Add(image);
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var badge = Text(visual.Label, 10, true); badge.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(badge, 2); grid.Children.Add(badge);
            results.Items.Add(row);
        }
        if (results.Items.Count > 0)
        {
            var index = previous is null ? -1 : results.Items.Cast<ListBoxItem>().ToList()
                .FindIndex(item => ReferenceEquals(item.Tag, previous));
            results.SelectedIndex = index >= 0 ? index : 0;
        }
        empty.Visibility = results.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)empty.Children[1]).Text = Loc.T(string.IsNullOrWhiteSpace(query.Text) ? "searchEmptyTitle" : "searchNoResults");
        ((TextBlock)empty.Children[2]).Text = Loc.T(string.IsNullOrWhiteSpace(query.Text) ? "searchEmptyHint" : "searchTryAnother");
    }

    void UpdateScopeLabel()
    {
        scope.IsEnabled = filter != SearchCategory.Applications;
        scope.Content = Text(filter == SearchCategory.Applications ? Loc.T("searchApplicationScope") : scopeName + "  ▾", 11, true);
        scope.ToolTip = filter == SearchCategory.Applications ? Loc.T("searchApplicationScope") : scopeFolder ?? Loc.T("searchScopeAll");
        System.Windows.Automation.AutomationProperties.SetName(scope, Loc.T("searchScope") + " · " + scopeName);
    }

    void OpenScope()
    {
        var menu = new ContextMenu { PlacementTarget = scope, Resources = Resources };
        void Add(string key, string? folder)
        {
            var item = new MenuItem { Header = Loc.T(key) };
            item.Click += (_, _) => ChooseScope(folder, Loc.T(key)); menu.Items.Add(item);
        }
        Add("searchScopeAll", null);
        Add("searchScopeDesktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("searchScopeDocuments", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        using (var folders = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
        {
            var downloads = folders?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            if (!string.IsNullOrEmpty(downloads)) Add("searchScopeDownloads", Environment.ExpandEnvironmentVariables(downloads));
        }
        var custom = new MenuItem { Header = Loc.T("searchScopeChoose") };
        custom.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("searchScopeChoose"), Multiselect = false };
            if (picker.ShowDialog(this) == true) ChooseScope(picker.FolderName, Path.GetFileName(picker.FolderName.TrimEnd('\\')));
        };
        menu.Items.Add(custom); PopupInteraction.Open(menu, scope);
    }

    void ChooseScope(string? folder, string name)
    {
        if (folder is not null && !Directory.Exists(folder)) { Status.Text = Loc.T("searchInvalidScope"); return; }
        scopeFolder = folder; scopeName = name; UpdateScopeLabel(); _ = SearchAsync();
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
