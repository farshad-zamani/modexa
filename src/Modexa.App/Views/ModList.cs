using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Modexa.App.Services;
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
    public bool IsSetup => Record?.Setup != null;
    /// <summary>Can be turned off for a while (a mod in the game; prepare steps have their own buttons).</summary>
    public bool CanDisable => Record != null && Record.Setup == null;

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
}

/// <summary>What a list's buttons do (the page runs them with its busy overlay).</summary>
public interface IModListHost
{
    void Run(ModAction action, IReadOnlyList<ModItem> items, bool single);
}

/// <summary>
/// Builds an installed-mods list (game pages and My Mods): a selection bar on top (select all,
/// disable / enable / remove the ticked mods), then one row per mod with a tick box and its own
/// Disable / Enable and Remove buttons.
/// </summary>
public static class ModList
{
    public static void Fill(Panel target, FrameworkElement owner, IReadOnlyList<ModItem> items, ModSelection selection,
        IModListHost host, bool openFolderButton = false)
    {
        target.Children.Clear();
        selection.KeepOnly(items.Select(i => i.Id));
        if (items.Count == 0) return;
        if (items.Count > 1) target.Children.Add(BuildBar(owner, items, selection, host));
        foreach (var item in items)
            target.Children.Add(ModRow.Build(owner, item, selection, host, openFolderButton));
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
        title.Children.Add(Chip(owner, record?.Setup != null ? null : kindLabel, record?.Setup != null ? "Mods_Kind_Setup" : null, "Accent"));
        if (off) title.Children.Add(Chip(owner, null, "Mods_Disabled_Chip", "Warning"));
        info.Children.Add(title);

        var parts = new List<string>();
        string? version = record != null ? record.Version : disabled!.Version;
        if (!string.IsNullOrWhiteSpace(version)) parts.Add(record?.Setup == null ? "v" + version : version!);
        string? author = record != null ? record.Author : disabled!.Author;
        if (!string.IsNullOrWhiteSpace(author)) parts.Add(author!);
        parts.Add(item.InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (record != null) parts.Add(Loc.Instance.Format("Mods_Changes", record.Files.Count + record.ArchiveEdits.Count));
        else parts.Add(Loc.Instance["Mods_Disabled_Meta"]);
        string? source = record != null ? record.Source : disabled!.Source;
        if (!string.IsNullOrWhiteSpace(source)) parts.Add(source!);
        var meta = new TextBlock
        {
            // Each part ends with a left-to-right mark, so a Persian part ("2 تغییر") never pulls the
            // numbers of the next one ("206.oiv") into right-to-left order.
            Text = string.Join("‎  ·  ", parts) + "‎",
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
