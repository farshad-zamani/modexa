using System.Windows.Media;
using System.Windows.Media.Imaging;
using Modexa.Core.Games;

namespace Modexa.App.Services;

/// <summary>
/// Each game's icon and artwork (assets\games\&lt;key&gt;.png / .jpg, from the game's store assets),
/// for the home cards and the faint backdrop of the game's own page. Images are decoded once.
/// </summary>
public static class GameArt
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>"games/gtav.png" -> "gtav".</summary>
    private static string Key(GameId id)
    {
        string asset = GameCatalog.Get(id).LogoAsset;
        int slash = asset.LastIndexOf('/'), dot = asset.LastIndexOf('.');
        return dot > slash ? asset[(slash + 1)..dot] : asset[(slash + 1)..];
    }

    /// <summary>Square game icon (96 px).</summary>
    public static ImageSource? Icon(GameId id) => Load($"assets/games/{Key(id)}.png", null, false);

    /// <summary>Wide artwork (1280 px); <paramref name="decodeWidth"/> keeps small uses light, <paramref name="gray"/> for "coming soon".</summary>
    public static ImageSource? Artwork(GameId id, int? decodeWidth = null, bool gray = false)
        => Load($"assets/games/{Key(id)}.jpg", decodeWidth, gray);

    /// <summary>Where the artwork's subject sits (the side to keep when it is cropped).</summary>
    public static AlignmentX ArtAlignment(GameId id) => id switch
    {
        GameId.GtaIV or GameId.EuroTruckSimulator2 or GameId.Cyberpunk2077 or GameId.RedDeadRedemption1
            or GameId.RedDeadRedemption2 or GameId.GtaSanAndreas => AlignmentX.Right,
        _ => AlignmentX.Center
    };

    private static ImageSource? Load(string path, int? decodeWidth, bool gray)
    {
        string cacheKey = $"{path}|{decodeWidth}|{gray}";
        lock (Cache)
        {
            if (Cache.TryGetValue(cacheKey, out var hit)) return hit;
            ImageSource? img = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/Modexa;component/{path}");
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth is > 0) bmp.DecodePixelWidth = decodeWidth.Value;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
                if (gray)
                {
                    var g = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                    g.Freeze();
                    img = g;
                }
            }
            catch { /* art is decoration: the card / page works without it */ }
            Cache[cacheKey] = img;
            return img;
        }
    }
}
