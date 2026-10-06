<div align="center">

# MODEXA

**Universal Mod Installer & Game Preparer for Rockstar Games**

Prepare *any version* of your game for mods in one click — then install mods without OpenIV.

[English](README.md) · [فارسی](docs/README.fa.md)

</div>

---

> **Modexa is not a new modding method.** It automates steps that already exist in the GTA modding
> community (the OpenIV `mods` folder, ScriptHook, version-matched `gameconfig.xml`, DLC `dlclist.xml`
> registration, etc.) and wraps them in one friendly app. All the underlying techniques, and the tools
> that pioneered them, are credited below.

## What it does

- **Prepare for mods, for your exact game version.** Modexa detects your game, edition (Legacy vs
  Enhanced) and build number, then downloads and installs the matching prerequisites — the ASI loader
  + ScriptHook runtime ("Mod Runner") and the right `gameconfig.xml` with heap/packfile adjusters
  ("GameConfig"). No more hunting for version-matched files.
- **Install mods without the OpenIV GUI.** Add-on (`dlc.rpf`) mods, loose/copy-paste mods and
  script mods are installed for you.
- **One-click menu install** and a **Clean launch** switch (safe for GTA Online).
- **Revert to vanilla** — every change Modexa makes is backed up and reversible.
- **English & Persian**, with a dark gaming interface.

> ⬇️ **Files are downloaded from the internet at runtime.** Modexa fetches the version-specific
> prerequisite bundles on demand and shows you the download progress.

## Editions

| | Free | Plus | Pro |
|---|---|---|---|
| Prepare every supported game for mods | ✅ | ✅ | ✅ |
| Install menu & prerequisites | ✅ | ✅ | ✅ |
| Install our licensed mods (`.mxa`) | — | ✅ | ✅ |
| Install any mod from any site | — | — | ✅ |
| Clean launch (safe for Online) & one-click revert | — | ✅ | ✅ |
| Theme | Dark | Dark + gold | Black + gold glow |

The **Free** edition is the one published here. Activating a license upgrades the *same* install to
Plus or Pro — and app updates always come through the Free build, so your license keeps working.

## Supported games

**Active now:** Grand Theft Auto V (Legacy & Enhanced), GTA San Andreas, GTA IV, Red Dead
Redemption, Red Dead Redemption 2, Cyberpunk 2077.
**Coming soon:** GTA: The Trilogy, Assetto Corsa, Euro Truck Simulator 2, Forza Horizon 5,
BeamNG.drive.

The right Mod Runner is picked automatically for your game's version/build.

## Credits

See [docs/CREDITS.md](docs/CREDITS.md). Built on the work of **OpenIV / OpenRPF**, **CodeWalker**,
**RPFXplorer**, **ScriptHookV / ScriptHookVDotNet**, **CLEO**, **Lenny's Mod Loader** and
**Mod Loader (thelink2012)**.

---

Modexa is a third-party tool and is **not affiliated with or endorsed by Rockstar Games or
Take-Two Interactive**. All trademarks belong to their respective owners.

Developed by [CloudTart](https://www.CloudTart.com), commissioned by
[RockStarGame.ir](https://www.rockstargame.ir).
