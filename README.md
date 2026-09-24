# Unstable Portals: Metal Transport

A client-side Valheim mod for BepInEx that lets players transport metal and other normally restricted items through portals by paying a configurable toll. The default is **1 Surtling Core and 5 Greydwarf Eyes per successful trip**, with portal damage and audiovisual instability warning of the risk.

## Behavior

- Ordinary portal travel remains free.
- If the inventory contains one or more portal-restricted items, the player must also carry the configured toll. The default is 1 Surtling Core and 5 Greydwarf Eyes.
- The toll is consumed only after Valheim accepts the teleport.
- The price is per trip, not per stack, item, or unit of metal.
- By default, the source and destination portals each take damage equal to 10% of their maximum health. They can be repaired normally.
- When the toll is available, the portal's existing particle animation and light slowly pulse from dark orange to purple and back. By default, the glowing frame runes pulse in the opposite phase, suggesting unstable energy moving between the portal and its frame. No custom model or animation is added.
- The portal's existing sound also wobbles at a slightly deeper pitch while that instability effect is active. This uses the built-in audio source; no sound file is added.
- Ordinary items flagged by Valheim as non-teleportable count. This includes metals and ores. Items marked by Valheim as absolutely blocked remain blocked.
- Toll items inside a cart or container do not count; they must be in the player's inventory.

## Installation

1. Install the current **BepInExPack for Valheim**.
2. If upgrading from version 1.2.0 or earlier, delete the pre-rename DLL first. Do not leave both DLLs installed.
3. Create `BepInEx\plugins\UnstablePortalsMetalTransport` in the Valheim game folder.
4. Copy `UnstablePortalsMetalTransport.dll` into that folder.
5. Start Valheim through the mod manager or BepInEx-enabled launcher.

For multiplayer, install the mod on every player's client. A dedicated-server-only installation is not sufficient because the player's client owns this portal interaction.

## Configuration

After the game starts once with the mod installed, edit:

`BepInEx/config/com.kernelpanik.unstableportalsmetaltransport.cfg`

Version 1.2.2 completes the mod rename by changing its internal plugin identity. BepInEx therefore creates a new configuration file. After launching once, copy any customized values from your previous configuration into the new file.

The available settings are:

| Section | Setting | Default | Purpose |
|---|---|---:|---|
| Toll | `SurtlingCoreCost` | `1` | Surtling Cores consumed per paid trip. |
| Toll | `GreydwarfEyeCost` | `5` | Greydwarf Eyes consumed per paid trip. |
| Portal Damage | `DamagePercent` | `10` | Percent of maximum portal health removed from each enabled side. |
| Portal Damage | `DamageSourcePortal` | `true` | Damage the portal the player enters. |
| Portal Damage | `DamageDestinationPortal` | `true` | Damage the linked portal the player arrives at. |
| Visuals | `InverseRunePulse` | `true` | Pulse the glowing frame runes opposite the portal particles and light. Set to `false` to synchronize them. |
| Visuals | `RuneBrightnessMultiplier` | `3` | HDR brightness of the pulsing frame runes. Increase if they appear dim; decrease if bloom is excessive. |
| Audio | `EnableUnstableSound` | `true` | Pitch-wobble the existing portal sound during the toll effect. |
| Audio | `MinimumPitchMultiplier` | `0.82` | Deeper endpoint of the pitch wobble. Values below `1` lower the pitch. |
| Audio | `MaximumPitchMultiplier` | `0.96` | Upper endpoint of the pitch wobble. Values above `1` raise the pitch. |

Set either item cost or the damage percentage to `0` to disable that part. Set either portal-damage toggle to `false` to protect that side. In multiplayer, use the same configuration on every player's client for consistent tolls.

For a higher, more strained sound, try `MinimumPitchMultiplier = 1.04` and `MaximumPitchMultiplier = 1.18`. Set both pitch values to the same number for a steady pitch shift instead of a wobble.

## Build from source

Requirements:

- The current PC version of Valheim
- BepInExPack for Valheim installed in the game directory
- .NET SDK capable of targeting .NET Framework 4.6.2

From PowerShell:

```powershell
.\build.ps1
```

If Valheim is not in the default Steam location:

```powershell
.\build.ps1 -ValheimPath "D:\SteamLibrary\steamapps\common\Valheim"
```

The output DLL is written to `bin\Release\net462\UnstablePortalsMetalTransport.dll`.

## Compatibility notes

The mod uses BepInEx and Harmony but does not require Jötunn. It resolves Valheim gameplay types at runtime so moving those types between game assemblies does not by itself break the mod. Other mods that completely replace portal logic may conflict.

Version 1.2.2 targets Valheim 1.0.14's current portal flow. Keep a normal world/character backup before testing any new mod.

## Uninstall

Delete `UnstablePortalsMetalTransport.dll` from `BepInEx\plugins`. The mod does not add items or save custom world data.
