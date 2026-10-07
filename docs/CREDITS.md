# Credits & Thanks

Modexa automates what the modding community built. Thank you to everyone below.
Formal license texts: [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt).

## Projects that shaped Modexa

- **[CodeWalker](https://github.com/dexyfex/CodeWalker)** (dexyfex) — the reference for GTA V RPF7
  archives. Modexa's "copy to mods folder and convert to OPEN" follows its approach, and official
  builds use its key bundle, which only opens with the user's own GTA V executable.
- **Neodymium** — the GTA V NG / AES archive cryptography (MIT) that Modexa re-implements.
- **[CodeWalkerProjects](https://github.com/crxhvrd/CodeWalkerProjects)** (crxhvrd) — its standalone
  OIV installer and uninstaller were the guide for Modexa's OIV support: mods-folder handling,
  the `.oivs` super-package format, and reversing XML / text edits on uninstall. Modexa took the
  ideas further: per-edit journals, handing originals over between mods so they can be removed in
  any order, and exact byte-for-byte restores.
- **OpenIV** and **OpenIV.asi / OpenRPF** — the `mods` folder convention, the OIV package format and
  the runtime hook.
- **RPFXplorer** (lucienlmy, MIT) — a clean RPF7 format reference.

## Bundled

- **SharpCompress** (MIT) — RAR/ZIP/7z extraction of the prerequisite packs.
- Fonts under the SIL OFL 1.1: **Vazirmatn** (Saber Rastikerdar), **Chakra Petch** (Cadson Demak),
  **Rajdhani** (Indian Type Foundry).
- Official builds: **OpenIV.asi** and its ASI loader (OpenIV team).

## Downloaded at runtime (not bundled)

The free prerequisite packs are hosted by **[WTMod.com](https://wtmod.com)** and contain
**ScriptHookV** (Alexander Blade), **ScriptHookVDotNet**, the **Lua** plugin, **OpenRPF**,
**Menyoo** (MAFINS), the **heap / packfile limit adjusters**, community **gameconfig.xml** files,
**CLEO**, **Lenny's Mod Loader** and **Mod Loader** (thelink2012).

All game trademarks belong to their respective owners. Modexa is a third-party tool and is not
affiliated with or endorsed by Rockstar Games, Take-Two Interactive or CD PROJEKT.

Developed by [cloudtart.com](https://cloudtart.com) · Made for [wtmod.com](https://wtmod.com)
