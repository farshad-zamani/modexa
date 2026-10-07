<div align="center">

# MODEXA

**Universal mod installer and game preparer for Rockstar games and more**

Prepare your exact game version for mods in one click, install OpenIV packages without OpenIV,
and remove any mod again just as cleanly.

[Download](https://github.com/farshad-zamani/modexa/releases/latest) · [English](README.md) · [فارسی](docs/README.fa.md)

</div>

---

> **Modexa is not a new modding method.** It automates what the GTA modding community already does
> (the OpenIV `mods` folder, ScriptHook, version-matched `gameconfig.xml`, `dlclist.xml`
> registration, OIV packages…) and wraps it in one friendly app. The tools and projects this work
> stands on are credited [below](#thanks).

## Download & install

1. Download **`Modexa-Setup-<version>.exe`** from the
   [latest release](https://github.com/farshad-zamani/modexa/releases/latest).
2. Run it. Modexa installs per user (no administrator rights) to `%LocalAppData%\Programs\Modexa`.
3. Start Modexa, pick your language (English / فارسی), then choose a game on the home screen.

Windows 10/11 x64. The installer is not code-signed yet, so Windows SmartScreen may show
"Windows protected your PC": choose **More info → Run anyway**.

## What it does

### Prepare a game for mods
Modexa detects the game folder, the edition (GTA V **Legacy** or **Enhanced**) and the exact
version/build, then downloads and installs the matching prerequisites, showing the progress:

| Game | What gets installed |
|---|---|
| GTA V Legacy / Enhanced | Mod Runner (ASI loader, ScriptHookV, ScriptHookVDotNet, Lua, OpenIV.asi / OpenRPF), GameConfig for add-on mods with your choice of traffic (+ heap and packfile adjusters on Legacy), Menyoo trainer |
| GTA San Andreas, GTA IV, Red Dead Redemption, Red Dead Redemption 2, Cyberpunk 2077 | The Mod Runner pack made for your game version |

Every file Modexa overwrites is backed up first; **Revert** restores the vanilla game.

### Editable `update.rpf`, created automatically
`gameconfig.xml`, add-on mods and most OIV packages need an editable copy of the game's
`update\update.rpf` in the `mods` folder. Modexa makes it itself, exactly like OpenIV's
"Copy to mods folder": the archive is copied to `mods\update\` and converted to the OPEN format.
The decryption keys come from **your own** `GTA5.exe` / `GTA5_Enhanced.exe`; the original game
files are never modified.

### Install OpenIV packages (`.oiv`, `.oivs`) — free
On a GTA V page, under **Install mods**, drag an `.oiv` onto the drop zone or choose the file.
Modexa shows the package name, version and author, asks for confirmation, and installs it:

- files for `update\`, `x64\` or any `.rpf` always go to the `mods` folder (never into the original
  game files);
- archive operations at any nesting depth: add / replace / delete files (binary, resource and nested
  `.rpf`), XML edits (add *First / Last / Before / After*, replace, remove) and text edits (add,
  insert, replace, delete);
- `.oivs` "super" packages install with their default selection;
- if something fails part-way, everything already done is rolled back.

### Uninstall any mod — free
Every install is recorded precisely: the original of each file it changed, a fingerprint of what it
wrote, every individual XML/text edit, and the folders it created. **Uninstall** plays this back in
reverse:

- a file nobody changed since is restored exactly (byte for byte);
- a shared file such as `dlclist.xml` only loses *this* mod's edits — other mods' entries stay;
- if a mod installed later replaced the same file, it inherits the original, so removing mods in
  **any order** always ends at the true original;
- folders the mod created are removed when empty; if the game is running (files locked), Modexa says
  so, and pressing Uninstall again finishes the job.

The **Installed mods** list on each game page shows what is installed there, each with its own
Uninstall button. **My Mods** shows everything installed on every game in one place.

### Also
- **Open folder** next to every game path opens it in File Explorer.
- Drag a mod file onto the window or double-click an `.mxa` in Explorer: Modexa opens the right
  game page and installs it there.
- English and Persian (Vazirmatn font), dark interface; Plus and Pro add gold themes with animated
  gold threads.

## Editions

| | Free | Plus | Pro |
|---|:-:|:-:|:-:|
| Prepare every supported game for mods | ✅ | ✅ | ✅ |
| Automatic editable `update.rpf` | ✅ | ✅ | ✅ |
| Install OpenIV packages (`.oiv`, `.oivs`) | ✅ | ✅ | ✅ |
| Uninstall mods (exact, any order) | ✅ | ✅ | ✅ |
| Licensed Modexa mods (`.mxa`) from [WTMod.com](https://wtmod.com) | — | ✅ | ✅ |
| Clean launch (safe for GTA Online) and one-click revert | — | ✅ | ✅ |
| Install any other mod: add-on `.rpf`, `.zip`, folders | — | — | ✅ |
| Manage all mods from My Mods | — | — | ✅ |
| Theme | Dark / cyan | Dark + gold | Black + gold glow |

The download here is the **Free** edition. A license upgrades the *same* installation to Plus or Pro,
and updates always come through this build, so a license keeps working after updating.

## Building from source

Requirements: Windows, .NET SDK 8 (pinned in `global.json`).

```powershell
dotnet build Modexa.sln -c Release
dotnet test tests\Modexa.Core.Tests
```

The public source builds the Free application. The licensed engine, the `.mxa` packer and the
server components are not part of this repository. Third-party binaries that official builds embed
(OpenIV mods-folder loader, the archive key bundle) are not committed either; without them a source
build can't create the editable `update.rpf` by itself.

Data lives in `%LocalAppData%\Modexa` (settings, logs, backups, the installed-mods list). Put an empty
`portable.txt` next to `Modexa.exe` to keep it next to the program instead.

## Thanks

Modexa would not exist without these projects and people. Thank you:

- **[CodeWalker](https://github.com/dexyfex/CodeWalker)** by dexyfex — the reference for GTA V
  RPF7 archives; Modexa's archive conversion follows its approach and uses its key bundle.
  The NG/AES archive cryptography is by **Neodymium** (MIT).
- **[CodeWalkerProjects](https://github.com/crxhvrd/CodeWalkerProjects)** by crxhvrd — its standalone
  OIV installer and uninstaller were the guide for Modexa's OIV support: copying archives to the
  mods folder and converting them to OPEN, the `.oivs` format, smart XML/text reversal on uninstall.
- **OpenIV** (and **OpenIV.asi / OpenRPF**) — the `mods` folder convention, the OIV package format
  and the runtime hook that makes modding GTA V possible.
- **RPFXplorer** (lucienlmy, MIT) — a clean RPF7 format reference.
- **ScriptHookV** (Alexander Blade), **ScriptHookVDotNet**, **Menyoo** (MAFINS), the heap and packfile
  limit adjusters, and the community `gameconfig.xml` authors.
- **CLEO**, **Lenny's Mod Loader** and **Mod Loader** (thelink2012) for GTA San Andreas.
- **[SharpCompress](https://github.com/adamhathcock/sharpcompress)** (MIT) — RAR/ZIP/7z extraction.
- Fonts: **Vazirmatn** (Saber Rastikerdar), **Chakra Petch** (Cadson Demak), **Rajdhani** (Indian
  Type Foundry), all SIL OFL 1.1.
- **[WTMod.com](https://wtmod.com)** — the free prerequisite packs Modexa installs.

Full notices: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and [docs/CREDITS.md](docs/CREDITS.md).

---

Modexa is a third-party tool and is **not affiliated with or endorsed by Rockstar Games or
Take-Two Interactive**. All trademarks belong to their respective owners. See [LICENSE](LICENSE).

Developed by [cloudtart.com](https://cloudtart.com) · Made for [wtmod.com](https://wtmod.com)
