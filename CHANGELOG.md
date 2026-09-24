# Changelog

## 1.2.2

- Completes the rename by changing the plugin GUID to `com.kernelpanik.unstableportalsmetaltransport`.
- Changes the generated config filename to `com.kernelpanik.unstableportalsmetaltransport.cfg`.
- Removes the remaining legacy project-name references from current source, documentation, and package metadata.
- Existing customized settings must be copied once from the previous config file.

## 1.2.1

- Renames the mod to **Unstable Portals: Metal Transport**.
- Renames the assembly, DLL, project, installation folder, and release packages to `UnstablePortalsMetalTransport`.
- Temporarily retains the preceding plugin GUID and config filename so existing 1.2.0 settings continue to work.
- Requires the pre-rename DLL to be removed during upgrade so BepInEx does not find duplicate copies of the same plugin.
- Adds Hexium/Thunderstore package metadata and a 256-by-256 package icon.

## 1.2.0

- Adds a configurable pitch wobble to the portal's existing sound while the restricted-item instability effect is active.
- Defaults to a deeper `0.82`-to-`0.96` pitch range; both endpoints can be configured or the audio effect can be disabled.
- Restores the portal's original pitch as soon as the toll effect ends.
- Changes the plugin identity and project author metadata to `KernelPanik`.
- Moves the generated config filename into the `KernelPanik` plugin namespace.

## 1.1.4

- Raises the frame-rune colors into HDR emission range so the inverse pulse remains visible instead of appearing black.
- Adds `Visuals.RuneBrightnessMultiplier`, default `3`, for adjusting rune brightness and bloom.

## 1.1.3

- Moves the instability animation from a Harmony patch on `TeleportWorld.Update()` to the mod's own reliable per-frame update loop.
- Keeps Valheim's normal half-second portal eligibility check while animating all active toll portals every frame.

## 1.1.2

- Pulses the portal frame emission/runes in the opposite color phase from the particles and light by default.
- Adds `Visuals.InverseRunePulse`; set it to `false` to make the frame, particles, and light pulse together.

## 1.1.1

- Replaces the static purple toll color with a smooth 2.6-second dark-orange-to-purple instability pulse.
- Applies the pulse to the existing portal emission, particles, and light without adding assets.
- Runs the visual pulse at frame rate while retaining the normal half-second eligibility check.

## 1.1.0

- Changes the default restricted-item toll to 1 Surtling Core and 5 Greydwarf Eyes.
- Damages both the source and destination portals by 10% of maximum health after a successful paid trip.
- Adds BepInEx configuration for both item costs, the damage percentage, and independent source/destination damage toggles.
- Queues destination damage until the linked portal loads, then applies it through Valheim's networked `WearNTear` damage path.

## 1.0.3

- Tints the existing portal model glow, particle animation, and light purple while nearby restricted cargo can pass by paying a Surtling Core.
- Restores the portal's original colors as soon as the toll condition no longer applies.
- Adds no custom models, textures, or animations.

## 1.0.2

- Correctly patches Valheim 1.0.14's `Humanoid.IsTeleportable(bool)` method, allowing the portal's normal active effect when the toll can be paid.
- Correctly patches the `Player.TeleportTo(...)` override so one Surtling Core is removed after a successful portal teleport.
- Does not charge when a teleport fails to start or when the portal/world already allows the cargo for free.

## 1.0.1

- Updated the patch points for Valheim 1.0, which removed `Humanoid.IsTeleportable()`.
- Hooks `TeleportWorldTrigger.OnTriggerEnter` and `Character.TeleportTo` so the Core is charged only when portal travel starts successfully.
- Temporarily relaxes eligible item restrictions only during the portal trigger, then restores them immediately.
- Keeps Valheim's absolute item restrictions blocked and keeps allow-all-items portals free.

## 1.0.0

- Initial release.
- Allows portal travel with Valheim-restricted inventory items when carrying a Surtling Core.
- Consumes one Surtling Core only after a portal teleport succeeds.
- Adds center-screen feedback for the requirement and successful payment.
