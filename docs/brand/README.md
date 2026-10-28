# Modexa logo

The neon "MX" tile is the Modexa logo: the app icon, the title bar, the installer, and the
product pages all use it. Made from the original artwork (`modexa-logo.png`, 1080 px).

| File | Size | Use |
| --- | --- | --- |
| `favicon.ico` | 16–256 px | Browser tab icon |
| `modexa-logo-32.png` | 32 px | Small icon (PNG favicon) |
| `modexa-logo-192.png` | 192 px | Android / web app icon, README header |
| `modexa-logo-512.png` | 512 px | Product page hero, store listings |
| `apple-touch-icon.png` | 180 px | iOS home screen |
| `modexa-social-1200x630.png` | 1200×630 | Link preview (Open Graph / Telegram / X) |

## On a web page

```html
<link rel="icon" href="/favicon.ico" sizes="any">
<link rel="icon" type="image/png" sizes="32x32" href="/modexa-logo-32.png">
<link rel="apple-touch-icon" href="/apple-touch-icon.png">
<meta property="og:image" content="https://example.com/modexa-social-1200x630.png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
```

## Rules

- Keep the tile as it is: no recolouring, stretching, outlines, or extra shadows. It is the same in
  light and dark themes.
- It reads best on dark backgrounds (`#06101A` to `#0D1A1F`). On white, give it room.
- Clear space around it: at least a quarter of its width on every side.
- Smallest size: 16 px as an icon, 24 px next to text. Next to the word **MODEXA**, the tile is
  about 1.8× the text's cap height (the app's title bar: 28 px tile, 15 px text).
- The wordmark is set in Chakra Petch Bold, all caps.
