using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Modexa.App.Services;
using Modexa.Core.Format;
using Modexa.Core.I18n;
using Modexa.Core.Install;

namespace Modexa.App.Views;

/// <summary>One entry of an installed-mods list: a mod in the game, or a mod the user disabled.</summary>
public sealed class ModItem
{
    public ModItem(ModInstallRecord record) => Record = record;
    public ModItem(DisabledMod disabled) => Disabled = disabled;

    public ModInstallRecord? Record { get; }
    public DisabledMod? Disabled { get; }

    public string Id => Record?.Id ?? Disabled!.Id;
    public string GameFolder => Record?.GameFolder ?? Disabled!.GameFolder;
    public DateTime InstalledUtc => Record?.InstalledUtc ?? Disabled!.InstalledUtc;
    public bool IsSetup => Record?.Setup != null || Disabled?.Setup != null;
    /// <summary>Can be turned off for a while (any mod in the game, prepare steps too).</summary>
    public bool CanDisable => Record != null;

    public string Name => Record != null ? ModInstallService.DisplayName(Record) : Disabled!.Name;
    public string? Author => Record != null ? Record.Author : Disabled!.Author;
    public string? Source => Record != null ? Record.Source : Disabled!.Source;
    /// <summary>What kind of mod it is, as the filter chips name it (OIV, Script, ASI, Setup…).</summary>
    public string Kind => IsSetup ? "Setup" : Record != null ? Record.KindLabel : Disabled!.Kind;

    /// <summary>Another part of the same split mod (see <see cref="ModParts"/>).</summary>
    public bool IsPartWith(ModItem other)
        => ModType == MxaModType.Oiv && other.ModType == MxaModType.Oiv && !IsSetup && !other.IsSetup
           && ProductId == null && other.ProductId == null
           && InstalledModsStore.SameFolder(GameFolder, other.GameFolder)
           && ModParts.SameMod(RawName, Author, Source, other.RawName, other.Author, other.Source,
               installedTogether: Math.Abs((InstalledUtc - other.InstalledUtc).TotalMinutes) <= 30);

    // Explicit (no "?? Disabled!.x" chains: a record's null field must not fall through to Disabled).
    private MxaModType ModType => Record != null ? Record.ModType : Disabled!.ModType;
    private string? ProductId => Record != null ? Record.ProductId : Disabled!.ProductId;
    private string RawName => Record != null ? Record.Name : Disabled!.Name;

    /// <summary>Everything in one game folder, newest first (a disabled mod keeps its place).</summary>
    public static List<ModItem> ForFolder(string gameFolder)
        => InstalledModsStore.ForFolder(gameFolder).Select(r => new ModItem(r))
            .Concat(DisabledModsStore.ForFolder(gameFolder).Select(d => new ModItem(d)))
            .OrderByDescending(i => i.InstalledUtc)
            .ToList();

    /// <summary>Everything on every game, newest first.</summary>
    public static List<ModItem> All()
        => InstalledModsStore.All().Select(r => new ModItem(r))
            .Concat(DisabledModsStore.All().Select(d => new ModItem(d)))
            .OrderByDescending(i => i.InstalledUtc)
            .ToList();
}

/// <summary>The ticked rows of one list.</summary>
public sealed class ModSelection
{
    private readonly HashSet<string> _ids = new();
    public event Action? Changed;

    public bool Contains(string id) => _ids.Contains(id);
    public int Count => _ids.Count;

    public void Set(string id, bool on)
    {
        if (on ? _ids.Add(id) : _ids.Remove(id)) Changed?.Invoke();
    }

    public void SetAll(IEnumerable<string> ids, bool on)
    {
        bool changed = false;
        foreach (var id in ids) changed |= on ? _ids.Add(id) : _ids.Remove(id);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Drops ids that are no longer listed (after a refresh).</summary>
    public void KeepOnly(IEnumerable<string> ids)
    {
        var keep = ids.ToHashSet();
        if (_ids.RemoveWhere(id => !keep.Contains(id)) > 0) Changed?.Invoke();
    }

    public List<ModItem> Of(IEnumerable<ModItem> items) => items.Where(i => _ids.Contains(i.Id)).ToList();

    // What else the list keeps across refreshes: the filter chip, the search text, and the open groups.
    public string? Kind { get; set; }
    public string Query { get; set; } = "";
    public HashSet<string> OpenGroups { get; } = new();
}

/// <summary>What a list's buttons do (the page runs them with its busy overlay).</summary>
public interface IModListHost
{
    void Run(ModAction action, IReadOnlyList<ModItem> items, bool single);
}

/// <summary>
/// Builds an installed-mods list (game pages and My Mods): kind filter chips and a search box (when
/// the list is long or mixed), a selection bar (select all, disable / enable / remove the ticked
/// mods), then one row per mod with a tick box and its own Disable / Enable and Remove buttons. The
/// parts of one split mod show as one row ("Mod — 3 parts") that opens to its parts.
/// </summary>
public static class ModList
{
    /// <summary>Search box from this many mods on.</summary>
    private const int SearchFrom = 5;

    public static void Fill(Panel target, FrameworkElement owner, IReadOnlyList<ModItem> items, ModSelection selection,
        IModListHost host, bool openFolderButton = false)
    {
        target.Children.Clear();
        selection.KeepOnly(items.Select(i => i.Id));
        if (items.Count == 0) return;

        var kinds = items.GroupBy(i => i.Kind).OrderBy(g => KindOrder(g.Key)).Select(g => (g.Key, g.Count())).ToList();
        if (selection.Kind != null && kinds.All(k => k.Key != selection.Kind)) selection.Kind = null;

        var results = new StackPanel();
        void Show()
        {
            var shown = items.Where(i => (selection.Kind == null || i.Kind == selection.Kind) && Matches(i, selection.Query)).ToList();
            selection.KeepOnly(shown.Select(i => i.Id)); // never act on rows the filter hides
            results.Children.Clear();
            if (shown.Count == 0)
            {
                var none = new TextBlock { Style = (Style)owner.FindResource("Text.Desc"), Margin = new Thickness(4, 6, 4, 14) };
                none.Bind(TextBlock.TextProperty, "Mods_NoMatch");
                results.Children.Add(none);
                return;
            }
            if (shown.Count > 1) results.Children.Add(BuildBar(owner, shown, selection, host));
            foreach (var parts in Groups(shown))
                results.Children.Add(parts.Count == 1
                    ? ModRow.Build(owner, parts[0], selection, host, openFolderButton)
                    : ModRow.BuildGroup(owner, parts, selection, host, openFolderButton));
        }

        bool search = items.Count >= SearchFrom || selection.Query.Length > 0;
        if (search || kinds.Count > 1) target.Children.Add(BuildTools(owner, items.Count, kinds, search, selection, Show));
        target.Children.Add(results);
        Show();
    }

    private static bool Matches(ModItem item, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string q = query.Trim();
        return item.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || (item.Source != null && System.IO.Path.GetFileName(item.Source).Contains(q, StringComparison.CurrentCultureIgnoreCase))
               || (item.Author?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
               || item.Kind.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static int KindOrder(string kind)
    {
        int i = Array.IndexOf(new[] { "OIV", "Add-on", "Modexa", "Script", "ASI", "Plugin", "Manual", "Files", "Setup" }, kind);
        return i < 0 ? 50 : i;
    }

    /// <summary>The rows, with the parts of each split mod together (in part order), newest mod first.</summary>
    internal static List<List<ModItem>> Groups(IReadOnlyList<ModItem> items)
    {
        var groupOf = new int[items.Count];
        for (int i = 0; i < items.Count; i++) groupOf[i] = i;
        int Root(int i) { while (groupOf[i] != i) i = groupOf[i] = groupOf[groupOf[i]]; return i; }
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
                if (Root(i) != Root(j) && items[i].IsPartWith(items[j])) groupOf[Root(j)] = Root(i);

        return Enumerable.Range(0, items.Count)
            .GroupBy(Root)
            .Select(g => g.Select(i => items[i]).ToList())
            .Select(g => g.Count == 1 ? g : ModParts.InInstallOrder(g, i => System.IO.Path.GetFileName(i.Source ?? i.Name)))
            .ToList();
    }

    /// <summary>Kind chips (All / OIV / Script …) and the search box.</summary>
    private static FrameworkElement BuildTools(FrameworkElement owner, int total, List<(string Key, int Count)> kinds, bool search,
        ModSelection selection, Action show)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (kinds.Count > 1)
        {
            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            string group = "kind" + Guid.NewGuid().ToString("N");
            void AddChip(string? kind, int count)
            {
                var chip = new RadioButton { Style = (Style)owner.FindResource("FilterChip"), GroupName = group, IsChecked = selection.Kind == kind, Margin = new Thickness(0, 2, 6, 2) };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                if (kind == null || kind == "Setup")
                {
                    label.Bind(TextBlock.TextProperty, kind == null ? "Mods_Kind_All" : "Mods_Kind_Setup");
                    label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Hud");
                }
                else label.Text = kind;
                content.Children.Add(label);
                var n = new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), Opacity = 0.65, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(n);
                chip.Content = content;
                chip.Checked += (_, _) => { if (selection.Kind == kind) return; selection.Kind = kind; show(); };
                chips.Children.Add(chip);
            }
            AddChip(null, total);
            foreach (var (key, count) in kinds) AddChip(key, count);
            grid.Children.Add(chips);
        }

        if (search)
        {
            var box = new TextBox { Style = (Style)owner.FindResource("SearchBox"), Width = 230, Text = selection.Query, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            box.SetBinding(FrameworkElement.TagProperty, Tr.B("Mods_Search"));
            box.TextChanged += (_, _) => { selection.Query = box.Text; show(); };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && box.Text.Length > 0) { box.Clear(); e.Handled = true; } };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }
        return grid;
    }

    private static FrameworkElement BuildBar(FrameworkElement owner, IReadOnlyList<ModItem> items, ModSelection selection, IModListHost host)
    {
        var bar = new Border
        {
            Style = (Style)owner.FindResource("Card"),
            Padding = new Thickness(18, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var all = new CheckBox { Style = (Style)owner.FindResource("SelectBox"), IsThreeState = false, VerticalAlignment = VerticalAlignment.Center };
        all.Bind(ContentControl.ContentProperty, "Mods_SelectAll");
        grid.Children.Add(all);

        var count = new TextBlock { Style = (Style)owner.FindResource("Text.Desc"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var disable = SmallButton(owner, "GhostButton.Small", "", "Mods_Disable");
        var enable = SmallButton(owner, "GhostButton.Small", "", "Mods_Enable");
        var remove = SmallButton(owner, "DangerButton.Small", "", "Mods_RemoveSelected");
        disable.Margin = enable.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(disable);
        actions.Children.Add(enable);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        bar.Child = grid;

        bool syncing = false;
        void Sync()
        {
            var picked = selection.Of(items);
            syncing = true;
            all.IsChecked = picked.Count == 0 ? false : picked.Count == items.Count ? true : null;
            syncing = false;
            count.Text = picked.Count == 0 ? "" : Loc.Instance.Format("Mods_Selected", picked.Count);
            remove.IsEnabled = picked.Count > 0;
            disable.IsEnabled = picked.Any(i => i.CanDisable);
            enable.IsEnabled = picked.Any(i => i.Disabled != null);
            disable.Visibility = items.Any(i => i.CanDisable) ? Visibility.Visible : Visibility.Collapsed;
            enable.Visibility = items.Any(i => i.Disabled != null) ? Visibility.Visible : Visibility.Collapsed;
        }
        all.Click += (_, _) =>
        {
            if (syncing) return;
            // A click on a partly ticked box clears it; otherwise it ticks / unticks everything.
            bool on = selection.Of(items).Count == 0;
            selection.SetAll(items.Select(i => i.Id), on);
        };
        selection.Changed += Sync;
        bar.Unloaded += (_, _) => selection.Changed -= Sync;
        bar.Loaded += (_, _) => { selection.Changed -= Sync; selection.Changed += Sync; Sync(); };
        Sync();

        disable.Click += (_, _) => host.Run(ModAction.Disable, selection.Of(items).Where(i => i.CanDisable).ToList(), single: false);
        enable.Click += (_, _) => host.Run(ModAction.Enable, selection.Of(items).Where(i => i.Disabled != null).ToList(), single: false);
        remove.Click += (_, _) => host.Run(ModAction.Remove, selection.Of(items), single: false);
        return bar;
    }

    internal static Button SmallButton(FrameworkElement owner, string style, string glyph, string textKey)
    {
        var b = new Button { Style = (Style)owner.FindResource(style) };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Style = (Style)owner.FindResource("Icon"), FontSize = 13, Text = glyph });
        var text = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.TextProperty, textKey);
        content.Children.Add(text);
        b.Content = content;
        return b;
    }
}

/// <summary>One row of an installed-mods list (shared by the game pages and My Mods).</summary>
public static class ModRow
{
    public static FrameworkElement Build(FrameworkElement owner, ModItem item, ModSelection selection, IModListHost host, bool openFolderButton)
    {
        var record = item.Record;
        var disabled = item.Disabled;
        bool off = disabled != null;

        var card = new Border
        {
            Style = (Style)owner.FindResource("Card"),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Tick box (bulk actions).
        var tick = new CheckBox
        {
            Style = (Style)owner.FindResource("SelectBox"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            IsChecked = selection.Contains(item.Id)
        };
        tick.Click += (_, _) => selection.Set(item.Id, tick.IsChecked == true);
        void Sync() => tick.IsChecked = selection.Contains(item.Id);
        tick.Loaded += (_, _) => { selection.Changed -= Sync; selection.Changed += Sync; Sync(); };
        tick.Unloaded += (_, _) => selection.Changed -= Sync;
        grid.Children.Add(tick);

        // The package's own icon when it has one, else a glyph for the kind.
        string kindLabel = record != null ? record.KindLabel : disabled!.Kind;
        var tile = new Border { Style = (Style)owner.FindResource("IconTile"), Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Opacity = off ? 0.5 : 1 };
        if (BusyOverlay.LoadImage(record != null ? record.IconFile : disabled!.IconFile, 80) is { } icon)
        {
            tile.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill, FlowDirection = FlowDirection.LeftToRight };
            tile.Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 10, 10);
        }
        else
        {
            var glyph = new TextBlock
            {
                Style = (Style)owner.FindResource("Icon"),
                Text = kindLabel switch { "OIV" => "", "Modexa" => "", "Add-on" => "", "Setup" => "", _ => "" }
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            tile.Child = glyph;
        }
        Grid.SetColumn(tile, 1);
        grid.Children.Add(tile);

        var info = new StackPanel { Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock
        {
            Text = record != null ? ModInstallService.DisplayName(record) : disabled!.Name,
            Style = (Style)owner.FindResource("Text.CardTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = off ? 0.6 : 1
        };
        title.Children.Add(name);
        bool setup = record?.Setup != null || disabled?.Setup != null;
        title.Children.Add(Chip(owner, setup ? null : kindLabel, setup ? "Mods_Kind_Setup" : null, "Accent"));
        if (off) title.Children.Add(Chip(owner, null, "Mods_Disabled_Chip", "Warning"));
        info.Children.Add(title);

        var parts = new List<string>();
        string? version = record != null ? record.Version : disabled!.Version;
        if (!string.IsNullOrWhiteSpace(version)) parts.Add(setup ? version! : "v" + version);
        string? author = record != null ? record.Author : disabled!.Author;
        if (!string.IsNullOrWhiteSpace(author)) parts.Add(author!);
        parts.Add(item.InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        // Short on purpose: the name is the title already (no file name repeated under it).
        if (record != null) parts.Add(Loc.Instance.Format("Mods_Changes", record.Files.Count + record.ArchiveEdits.Count));
        else parts.Add(Loc.Instance["Mods_Disabled_Meta"]);
        var meta = new TextBlock
        {
            // Each part ends with a left-to-right mark, so a Persian part ("2 تغییر") never pulls the
            // numbers of the next one ("206.oiv") into right-to-left order.
            Text = string.Join("\u200E · ", parts) + "\u200E",
            Style = (Style)owner.FindResource("Text.Desc"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            // File names, versions and dates are Latin: lay the line out LTR (aligned to the page side)
            // so a Persian page doesn't scramble "206.oiv" into "oiv.206".
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left
        };
        NumberSubstitution.SetSubstitution(meta, NumberSubstitutionMethod.European);
        info.Children.Add(meta);
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (openFolderButton)
        {
            var open = new Button { Style = (Style)owner.FindResource("IconButton"), Margin = new Thickness(0, 0, 8, 0) };
            open.Content = new TextBlock { Style = (Style)owner.FindResource("Icon"), FontSize = 15, Text = "" };
            open.SetBinding(FrameworkElement.ToolTipProperty, Tr.B("Common_OpenFolder"));
            open.Click += (_, _) => ModInstallService.OpenFolder(item.GameFolder);
            actions.Children.Add(open);
        }
        if (item.CanDisable)
        {
            var disable = ModList.SmallButton(owner, "GhostButton", "", "Mods_Disable");
            disable.Margin = new Thickness(0, 0, 8, 0);
            disable.SetBinding(FrameworkElement.ToolTipProperty, Tr.B("Mods_Disable_Tip"));
            disable.Click += (_, _) => host.Run(ModAction.Disable, new[] { item }, single: true);
            actions.Children.Add(disable);
        }
        if (off)
        {
            var enable = ModList.SmallButton(owner, "PrimaryButton", "", "Mods_Enable");
            enable.Margin = new Thickness(0, 0, 8, 0);
            enable.Click += (_, _) => host.Run(ModAction.Enable, new[] { item }, single: true);
            actions.Children.Add(enable);
        }
        var remove = new Button { Style = (Style)owner.FindResource("DangerButton") };
        remove.Bind(ContentControl.ContentProperty, "Mods_Uninstall");
        remove.Click += (_, _) => host.Run(ModAction.Remove, new[] { item }, single: true);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    /// <summary>
    /// The parts of one split mod as one row ("NaturalVision Evolved · 2 parts"): one tick box, one
    /// set of buttons for all parts, and an arrow that opens the parts (each with its own row).
    /// </summary>
    public static FrameworkElement BuildGroup(FrameworkElement owner, List<ModItem> parts, ModSelection selection, IModListHost host, bool openFolderButton)
    {
        var first = parts[0];
        bool allOff = parts.All(p => p.Disabled != null);
        string key = parts.Select(p => p.Id).Min(StringComparer.Ordinal)!;
        bool open = selection.OpenGroups.Contains(key);

        var outer = new StackPanel();
        var card = new Border
        {
            Style = (Style)owner.FindResource("Card"),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // One tick for all parts (partly ticked when only some parts are).
        var tick = new CheckBox { Style = (Style)owner.FindResource("SelectBox"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        bool syncing = false;
        void Sync()
        {
            int n = parts.Count(p => selection.Contains(p.Id));
            syncing = true;
            tick.IsChecked = n == 0 ? false : n == parts.Count ? true : null;
            syncing = false;
        }
        tick.Click += (_, _) =>
        {
            if (syncing) return;
            selection.SetAll(parts.Select(p => p.Id), !parts.All(p => selection.Contains(p.Id)));
            Sync();
        };
        tick.Loaded += (_, _) => { selection.Changed -= Sync; selection.Changed += Sync; Sync(); };
        tick.Unloaded += (_, _) => selection.Changed -= Sync;
        Sync();
        grid.Children.Add(tick);

        var tile = new Border { Style = (Style)owner.FindResource("IconTile"), Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Opacity = allOff ? 0.5 : 1 };
        var iconFile = parts.Select(p => p.Record != null ? p.Record.IconFile : p.Disabled!.IconFile).FirstOrDefault(f => !string.IsNullOrEmpty(f));
        if (BusyOverlay.LoadImage(iconFile, 80) is { } icon)
        {
            tile.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill, FlowDirection = FlowDirection.LeftToRight };
            tile.Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 10, 10);
        }
        else
        {
            var glyph = new TextBlock { Style = (Style)owner.FindResource("Icon"), Text = "" };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            tile.Child = glyph;
        }
        Grid.SetColumn(tile, 1);
        grid.Children.Add(tile);

        // Title (click opens / closes the parts).
        var info = new StackPanel { Margin = new Thickness(14, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = ModParts.BaseName(first.Name),
            Style = (Style)owner.FindResource("Text.CardTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = allOff ? 0.6 : 1
        });
        title.Children.Add(Chip(owner, first.Kind, null, "Accent"));
        var count = Chip(owner, null, null, "Text.Secondary");
        ((TextBlock)count.Child).SetResourceReference(TextBlock.FontFamilyProperty, "Font.Hud");
        ((TextBlock)count.Child).Text = Loc.Instance.Format("Mods_Parts", parts.Count);
        title.Children.Add(count);
        if (allOff) title.Children.Add(Chip(owner, null, "Mods_Disabled_Chip", "Warning"));
        info.Children.Add(title);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(first.Author)) meta.Add(first.Author!);
        meta.Add(parts.Max(p => p.InstalledUtc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        int changes = parts.Sum(p => p.Record != null ? p.Record.Files.Count + p.Record.ArchiveEdits.Count : 0);
        if (changes > 0) meta.Add(Loc.Instance.Format("Mods_Changes", changes));
        var metaText = new TextBlock
        {
            Text = string.Join("‎ · ", meta) + "‎",
            Style = (Style)owner.FindResource("Text.Desc"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = Loc.Instance.IsRtl ? TextAlignment.Right : TextAlignment.Left
        };
        NumberSubstitution.SetSubstitution(metaText, NumberSubstitutionMethod.European);
        info.Children.Add(metaText);
        Grid.SetColumn(info, 2);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var toggle = new Button { Style = (Style)owner.FindResource("IconButton"), Margin = new Thickness(0, 0, 8, 0) };
        var chevron = new TextBlock
        {
            Style = (Style)owner.FindResource("Icon"), FontSize = 12, Text = "",
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(open ? 180 : 0)
        };
        toggle.Content = chevron;
        toggle.SetBinding(FrameworkElement.ToolTipProperty, Tr.B("Mods_Parts_Show"));
        actions.Children.Add(toggle);
        if (openFolderButton)
        {
            var folder = new Button { Style = (Style)owner.FindResource("IconButton"), Margin = new Thickness(0, 0, 8, 0) };
            folder.Content = new TextBlock { Style = (Style)owner.FindResource("Icon"), FontSize = 15, Text = "" };
            folder.SetBinding(FrameworkElement.ToolTipProperty, Tr.B("Common_OpenFolder"));
            folder.Click += (_, _) => ModInstallService.OpenFolder(first.GameFolder);
            actions.Children.Add(folder);
        }
        if (parts.Any(p => p.CanDisable))
        {
            var disable = ModList.SmallButton(owner, "GhostButton", "", "Mods_Disable");
            disable.Margin = new Thickness(0, 0, 8, 0);
            disable.Click += (_, _) => host.Run(ModAction.Disable, parts.Where(p => p.CanDisable).ToList(), single: false);
            actions.Children.Add(disable);
        }
        if (parts.Any(p => p.Disabled != null))
        {
            var enable = ModList.SmallButton(owner, "PrimaryButton", "", "Mods_Enable");
            enable.Margin = new Thickness(0, 0, 8, 0);
            enable.Click += (_, _) => host.Run(ModAction.Enable, parts.Where(p => p.Disabled != null).ToList(), single: false);
            actions.Children.Add(enable);
        }
        var remove = new Button { Style = (Style)owner.FindResource("DangerButton") };
        remove.Bind(ContentControl.ContentProperty, "Mods_Uninstall");
        // Newest part first, so a later part's changes come off before the ones under them.
        remove.Click += (_, _) => host.Run(ModAction.Remove, parts.OrderByDescending(p => p.InstalledUtc).ToList(), single: false);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);
        card.Child = grid;
        outer.Children.Add(card);

        // The parts, indented under the group with a thin accent rule.
        var list = new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(14, 0, 0, 0),
            Margin = new Thickness(26, -2, 0, 8),
            Visibility = open ? Visibility.Visible : Visibility.Collapsed
        };
        list.SetResourceReference(Border.BorderBrushProperty, "Stroke.Accent");
        var rows = new StackPanel();
        foreach (var part in parts) rows.Children.Add(Build(owner, part, selection, host, openFolderButton: false));
        list.Child = rows;
        outer.Children.Add(list);

        void Toggle()
        {
            bool nowOpen = list.Visibility != Visibility.Visible;
            list.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            if (nowOpen) selection.OpenGroups.Add(key); else selection.OpenGroups.Remove(key);
            ((RotateTransform)chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(nowOpen ? 180 : 0, TimeSpan.FromMilliseconds(160)));
        }
        toggle.Click += (_, _) => Toggle();
        info.MouseLeftButtonUp += (_, _) => Toggle();
        return outer;
    }

    /// <summary>Small label chip: literal <paramref name="text"/> (Latin) or a localized <paramref name="key"/>.</summary>
    private static Border Chip(FrameworkElement owner, string? text, string? key, string brush)
    {
        var chip = new Border { Style = (Style)owner.FindResource("TierChip"), Margin = new Thickness(10, 0, 0, 0), MinHeight = 20, Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var t = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        if (key != null) t.Bind(TextBlock.TextProperty, key);
        else t.Text = text ?? "";
        t.SetResourceReference(TextBlock.FontFamilyProperty, key != null ? "Font.Hud" : "Font.En.Hud");
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (brush != "Accent") chip.SetResourceReference(Border.BorderBrushProperty, brush);
        chip.Child = t;
        return chip;
    }
}
