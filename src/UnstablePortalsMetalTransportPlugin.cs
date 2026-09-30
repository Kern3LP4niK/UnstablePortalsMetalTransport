using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace UnstablePortalsMetalTransport
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class UnstablePortalsMetalTransportPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kernelpanik.unstableportalsmetaltransport";
        public const string PluginName = "Unstable Portals: Metal Transport";
        public const string PluginVersion = "1.3.0";

        private Harmony _harmony;

        internal static ConfigEntry<int> SurtlingCoreCost { get; private set; }
        internal static ConfigEntry<int> GreydwarfEyeCost { get; private set; }
        internal static ConfigEntry<float> PortalDamagePercent { get; private set; }
        internal static ConfigEntry<bool> DamageSourcePortal { get; private set; }
        internal static ConfigEntry<bool> DamageDestinationPortal { get; private set; }
        internal static ConfigEntry<bool> PreventSourcePortalDestruction { get; private set; }
        internal static ConfigEntry<float> PlayerDamageMaxHealthPercent { get; private set; }
        internal static ConfigEntry<float> PlayerDamageCurrentHealthPercent { get; private set; }
        internal static ConfigEntry<float> PlayerDamageFlat { get; private set; }
        internal static ConfigEntry<bool> InverseRunePulse { get; private set; }
        internal static ConfigEntry<float> RuneBrightnessMultiplier { get; private set; }
        internal static ConfigEntry<bool> EnableUnstableSound { get; private set; }
        internal static ConfigEntry<float> MinimumPitchMultiplier { get; private set; }
        internal static ConfigEntry<float> MaximumPitchMultiplier { get; private set; }

        private void Awake()
        {
            SurtlingCoreCost = Config.Bind(
                "Toll",
                "SurtlingCoreCost",
                1,
                new ConfigDescription(
                    "Surtling Cores consumed after a successful restricted-item portal trip.",
                    new AcceptableValueRange<int>(0, 100)));
            GreydwarfEyeCost = Config.Bind(
                "Toll",
                "GreydwarfEyeCost",
                5,
                new ConfigDescription(
                    "Greydwarf Eyes consumed after a successful restricted-item portal trip.",
                    new AcceptableValueRange<int>(0, 1000)));
            PortalDamagePercent = Config.Bind(
                "Portal Damage",
                "DamagePercent",
                10f,
                new ConfigDescription(
                    "Percentage of each affected portal's maximum health removed per paid trip.",
                    new AcceptableValueRange<float>(0f, 100f)));
            DamageSourcePortal = Config.Bind(
                "Portal Damage",
                "DamageSourcePortal",
                true,
                "Damage the portal the player enters after a successful paid trip.");
            DamageDestinationPortal = Config.Bind(
                "Portal Damage",
                "DamageDestinationPortal",
                true,
                "Damage the linked destination portal after a successful paid trip.");
            PreventSourcePortalDestruction = Config.Bind(
                "Portal Damage",
                "PreventSourcePortalDestruction",
                false,
                "Block restricted-item travel before payment when the configured damage would destroy the source portal. This does not inspect or protect the destination portal.");
            PlayerDamageMaxHealthPercent = Config.Bind(
                "Player Damage",
                "MaxHealthPercent",
                0f,
                new ConfigDescription(
                    "Damage the player by this percentage of maximum health after a successful paid trip.",
                    new AcceptableValueRange<float>(0f, 100f)));
            PlayerDamageCurrentHealthPercent = Config.Bind(
                "Player Damage",
                "CurrentHealthPercent",
                0f,
                new ConfigDescription(
                    "Damage the player by this percentage of current health after a successful paid trip. All player-damage components use the same pre-damage health snapshot and are added together.",
                    new AcceptableValueRange<float>(0f, 100f)));
            PlayerDamageFlat = Config.Bind(
                "Player Damage",
                "FlatDamage",
                0f,
                new ConfigDescription(
                    "Flat player damage added after the percentage-based damage for a successful paid trip.",
                    new AcceptableValueRange<float>(0f, 10000f)));
            InverseRunePulse = Config.Bind(
                "Visuals",
                "InverseRunePulse",
                true,
                "Pulse the portal frame emission/runes in the opposite color phase from the particles and light.");
            RuneBrightnessMultiplier = Config.Bind(
                "Visuals",
                "RuneBrightnessMultiplier",
                3f,
                new ConfigDescription(
                    "HDR brightness multiplier for the pulsing frame emission/runes.",
                    new AcceptableValueRange<float>(0.5f, 10f)));
            EnableUnstableSound = Config.Bind(
                "Audio",
                "EnableUnstableSound",
                true,
                "Pitch-wobble the portal's existing sound while the restricted-item toll effect is active.");
            MinimumPitchMultiplier = Config.Bind(
                "Audio",
                "MinimumPitchMultiplier",
                0.82f,
                new ConfigDescription(
                    "Lowest pitch multiplier used by the instability wobble. Values below 1 sound deeper.",
                    new AcceptableValueRange<float>(0.5f, 1.5f)));
            MaximumPitchMultiplier = Config.Bind(
                "Audio",
                "MaximumPitchMultiplier",
                0.96f,
                new ConfigDescription(
                    "Highest pitch multiplier used by the instability wobble. Values above 1 sound higher.",
                    new AcceptableValueRange<float>(0.5f, 1.5f)));

            _harmony = new Harmony(PluginGuid);
            try
            {
                PortalPatches.Install(_harmony, Logger);
                Logger.LogInfo(PluginName + " " + PluginVersion + " loaded.");
            }
            catch (Exception exception)
            {
                Logger.LogError("Could not install portal patches: " + exception);
            }
        }

        private void Update()
        {
            PortalPatches.UpdatePortalVisuals();
            PortalPatches.UpdatePendingPortalDamage();
        }

        private void OnDestroy()
        {
            PortalPatches.RestorePortalVisuals();
            PortalPatches.ClearPendingPortalDamage();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }

    internal static class PortalPatches
    {
        private const string CorePrefabName = "SurtlingCore";
        private const string EyePrefabName = "GreydwarfEye";
        private static readonly TimeSpan PendingTripLifetime = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PendingDamageLifetime = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AudioCaptureRetryInterval = TimeSpan.FromSeconds(1);
        private static readonly ConditionalWeakTable<object, PendingTrip> PendingTrips =
            new ConditionalWeakTable<object, PendingTrip>();
        private static readonly ConditionalWeakTable<object, PortalVisualState> PortalVisualStates =
            new ConditionalWeakTable<object, PortalVisualState>();
        private static readonly List<WeakReference> ActiveVisualPortals = new List<WeakReference>();
        private static readonly List<PendingPortalDamage> PendingDestinationDamage =
            new List<PendingPortalDamage>();

        private static ManualLogSource _log;
        private static Type _playerType;
        private static Type _wearNTearType;
        private static Type _hitDataType;
        private static Type _zNetSceneType;
        private static Type _audioSourceType;
        private static DateTime _nextDamagePollUtc;

        [ThreadStatic]
        private static object _portalBeingUpdated;

        [ThreadStatic]
        private static bool _portalUpdateUsesToll;

        [ThreadStatic]
        private static bool _sourcePortalTravelBlocked;

        private sealed class PendingTrip
        {
            public DateTime ExpiresUtc;
            public object Inventory;
            public object SourcePortal;
            public object DestinationPortalId;
            public bool EligibilityOverrideUsed;
        }

        private sealed class PendingPortalDamage
        {
            public DateTime ExpiresUtc;
            public object DestinationPortalId;
            public float DamagePercent;
        }

        private sealed class InventorySnapshot
        {
            public object Inventory;
            public bool HasEligibleRestrictedItem;
            public bool HasAbsoluteRestriction;
            public int CoreCount;
            public int EyeCount;
        }

        private sealed class ParticleVisualState
        {
            public object Particle;
            public PropertyInfo MainProperty;
            public PropertyInfo StartColorProperty;
            public ConstructorInfo GradientConstructor;
            public object OriginalStartColor;
        }

        private sealed class AudioVisualState
        {
            public object AudioSource;
            public PropertyInfo PitchProperty;
            public float OriginalPitch;
        }

        private sealed class PortalVisualState
        {
            public FieldInfo TargetColorField;
            public object OriginalTargetColor;
            public ConstructorInfo ColorConstructor;
            public object Light;
            public PropertyInfo LightColorProperty;
            public object OriginalLightColor;
            public readonly List<ParticleVisualState> Particles = new List<ParticleVisualState>();
            public readonly List<AudioVisualState> AudioSources = new List<AudioVisualState>();
            public DateTime NextAudioCaptureUtc;
            public bool AudioPitchModified;
            public bool ListedAsActive;
            public bool DiagnosticsLogged;
            public bool AudioMissingDiagnosticLogged;
            public bool AudioRecoveryLogged;
            public bool TollEffectActive;
        }

        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            Type teleportWorldType = RequireType("TeleportWorld");
            Type humanoidType = RequireType("Humanoid");
            _playerType = RequireType("Player");
            _wearNTearType = RequireType("WearNTear");
            _hitDataType = RequireType("HitData");
            _zNetSceneType = RequireType("ZNetScene");
            _audioSourceType = AccessTools.TypeByName("UnityEngine.AudioSource");

            MethodInfo portalTeleport = teleportWorldType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "Teleport" &&
                    method.GetParameters().Any(parameter =>
                        parameter.ParameterType.IsAssignableFrom(_playerType) ||
                        _playerType.IsAssignableFrom(parameter.ParameterType)));
            if (portalTeleport == null)
            {
                throw new MissingMethodException("TeleportWorld.Teleport(Player)");
            }

            MethodInfo isTeleportable = humanoidType.GetMethod(
                "IsTeleportable",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(bool) },
                null);
            if (isTeleportable == null || isTeleportable.ReturnType != typeof(bool))
            {
                throw new MissingMethodException("Humanoid.IsTeleportable(bool)");
            }

            MethodInfo playerTeleportTo = _playerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "TeleportTo" &&
                    method.DeclaringType == _playerType && method.ReturnType == typeof(bool));
            if (playerTeleportTo == null)
            {
                throw new MissingMethodException("Player.TeleportTo(...)");
            }

            MethodInfo updatePortal = teleportWorldType.GetMethod(
                "UpdatePortal",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (updatePortal == null)
            {
                throw new MissingMethodException("TeleportWorld.UpdatePortal()");
            }

            HarmonyMethod portalPrefix = PatchMethod(nameof(PortalTeleportPrefix));
            HarmonyMethod portalPostfix = PatchMethod(nameof(PortalTeleportPostfix));
            HarmonyMethod teleportablePostfix = PatchMethod(nameof(HumanoidIsTeleportablePostfix));
            HarmonyMethod teleportToPostfix = PatchMethod(nameof(PlayerTeleportToPostfix));
            HarmonyMethod updatePortalPrefix = PatchMethod(nameof(PortalUpdatePrefix));
            HarmonyMethod updatePortalPostfix = PatchMethod(nameof(PortalUpdatePostfix));

            harmony.Patch(portalTeleport, prefix: portalPrefix, postfix: portalPostfix);
            harmony.Patch(isTeleportable, postfix: teleportablePostfix);
            harmony.Patch(playerTeleportTo, postfix: teleportToPostfix);
            harmony.Patch(updatePortal, prefix: updatePortalPrefix, postfix: updatePortalPostfix);

            _log.LogInfo("Patched " + portalTeleport.DeclaringType.Name + "." + portalTeleport.Name + ", " +
                         isTeleportable.DeclaringType.Name + "." + isTeleportable.Name + ", and " +
                         playerTeleportTo.DeclaringType.Name + "." + playerTeleportTo.Name +
                         "; toll eligibility is tracked through " + updatePortal.DeclaringType.Name + "." +
                         updatePortal.Name + " and pulsing visuals use the plugin frame loop.");
        }

        private static HarmonyMethod PatchMethod(string name)
        {
            return new HarmonyMethod(typeof(PortalPatches).GetMethod(
                name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Type RequireType(string name)
        {
            Type type = AccessTools.TypeByName(name);
            if (type == null)
            {
                throw new TypeLoadException("Could not find Valheim type " + name + ".");
            }
            return type;
        }

        private static void PortalTeleportPrefix(object __instance, object[] __args)
        {
            _sourcePortalTravelBlocked = false;
            object player = FindPlayer(__args);
            if (player == null || !IsLocalPlayer(player))
            {
                return;
            }

            RemovePendingTrip(player);
            if (ReadBoolField(__instance, "m_allowAllItems"))
            {
                return;
            }

            InventorySnapshot snapshot = ReadInventory(player);
            if (snapshot == null || !snapshot.HasEligibleRestrictedItem || snapshot.HasAbsoluteRestriction)
            {
                return;
            }

            if (!HasRequiredToll(snapshot))
            {
                ShowCenterMessage(player, "You need " + DescribeToll() +
                    " to use a portal while carrying metal.");
                return;
            }

            if (WouldDestroySourcePortal(__instance))
            {
                _sourcePortalTravelBlocked = true;
                ShowCenterMessage(player,
                    "Travel blocked: the instability would destroy this portal.");
                return;
            }

            SetPendingTrip(
                player,
                snapshot.Inventory,
                __instance,
                GetConnectedPortalId(__instance));
        }

        private static void PortalTeleportPostfix(object[] __args)
        {
            try
            {
                object player = FindPlayer(__args);
                if (player != null && HasPendingTrip(player))
                {
                    // A successful Player.TeleportTo postfix removes the trip first. If it is
                    // still present here, Valheim rejected or did not start the teleport.
                    RemovePendingTrip(player);
                }
            }
            finally
            {
                _sourcePortalTravelBlocked = false;
            }
        }

        private static void PortalUpdatePrefix(object __instance)
        {
            _portalBeingUpdated = __instance;
            _portalUpdateUsesToll = false;
        }

        private static void PortalUpdatePostfix(object __instance)
        {
            try
            {
                bool useTollEffect = ReferenceEquals(_portalBeingUpdated, __instance) &&
                    _portalUpdateUsesToll;
                SetPortalTollEffect(__instance, useTollEffect);
            }
            catch (Exception exception)
            {
                _log.LogDebug("Could not update the unstable portal effect: " + exception.Message);
            }
            finally
            {
                _portalBeingUpdated = null;
                _portalUpdateUsesToll = false;
            }
        }

        internal static void UpdatePortalVisuals()
        {
            if (ActiveVisualPortals.Count == 0)
            {
                return;
            }

            float purpleBlend = GetPulseBlend(DateTime.UtcNow);
            for (int index = ActiveVisualPortals.Count - 1; index >= 0; index--)
            {
                object portal = ActiveVisualPortals[index].Target;
                if (portal == null)
                {
                    ActiveVisualPortals.RemoveAt(index);
                    continue;
                }

                PortalVisualState state;
                if (!PortalVisualStates.TryGetValue(portal, out state) || !state.TollEffectActive)
                {
                    if (state != null)
                    {
                        state.ListedAsActive = false;
                    }
                    ActiveVisualPortals.RemoveAt(index);
                    continue;
                }

                try
                {
                    ApplyPortalPulseFrame(portal, state, purpleBlend);
                }
                catch (Exception exception)
                {
                    // A destroyed Unity object can leave a live managed wrapper briefly.
                    _log.LogDebug("Stopped animating a portal visual: " + exception.Message);
                    state.TollEffectActive = false;
                    state.ListedAsActive = false;
                    ActiveVisualPortals.RemoveAt(index);
                }
            }
        }

        private static void HumanoidIsTeleportablePostfix(object __instance, ref bool __result)
        {
            if (__result || __instance == null || !_playerType.IsInstanceOfType(__instance) ||
                !IsLocalPlayer(__instance))
            {
                return;
            }

            InventorySnapshot snapshot = ReadInventory(__instance);
            if (snapshot == null || !snapshot.HasEligibleRestrictedItem ||
                snapshot.HasAbsoluteRestriction || !HasRequiredToll(snapshot))
            {
                return;
            }

            if (_sourcePortalTravelBlocked ||
                (_portalBeingUpdated != null && WouldDestroySourcePortal(_portalBeingUpdated)))
            {
                return;
            }

            __result = true;

            if (_portalBeingUpdated != null)
            {
                _portalUpdateUsesToll = true;
            }

            PendingTrip trip = GetPendingTrip(__instance);
            if (trip != null)
            {
                // This distinguishes toll-enabled travel from portals/world settings that
                // already allow the inventory for free.
                trip.EligibilityOverrideUsed = true;
            }
        }

        private static void PlayerTeleportToPostfix(object __instance, ref bool __result)
        {
            if (!__result || __instance == null)
            {
                return;
            }

            PendingTrip trip = GetPendingTrip(__instance);
            if (trip == null || !trip.EligibilityOverrideUsed)
            {
                return;
            }

            if (!ConsumeToll(trip.Inventory))
            {
                _log.LogWarning("Portal travel succeeded, but the configured toll could not be removed.");
                RemovePendingTrip(__instance);
                return;
            }

            float damagePercent = ConfiguredDamagePercent;
            if (damagePercent > 0f && ConfiguredDamageSource)
            {
                ApplyPortalDamage(trip.SourcePortal, damagePercent);
            }
            if (damagePercent > 0f && ConfiguredDamageDestination && trip.DestinationPortalId != null)
            {
                QueueDestinationDamage(trip.DestinationPortalId, damagePercent);
            }

            RemovePendingTrip(__instance);
            string toll = DescribeToll();
            if (ConfiguredCoreCost > 0 || ConfiguredEyeCost > 0)
            {
                ShowCenterMessage(__instance, "The portal consumes " + toll + ".");
            }
            _log.LogInfo("Charged " + toll + " for restricted-item portal travel.");
            ApplyPlayerDamage(__instance);
        }

        private static object FindPlayer(object[] arguments)
        {
            if (arguments == null || _playerType == null)
            {
                return null;
            }
            return arguments.FirstOrDefault(argument =>
                argument != null && _playerType.IsInstanceOfType(argument));
        }

        private static bool IsLocalPlayer(object player)
        {
            FieldInfo localPlayerField = _playerType.GetField(
                "m_localPlayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object localPlayer = localPlayerField == null ? null : localPlayerField.GetValue(null);
            return localPlayer == null || ReferenceEquals(localPlayer, player);
        }

        private static void SetPortalTollEffect(object portal, bool active)
        {
            if (portal == null)
            {
                return;
            }

            PortalVisualState state;
            if (!PortalVisualStates.TryGetValue(portal, out state))
            {
                if (!active)
                {
                    return;
                }
                state = CapturePortalVisualState(portal);
                PortalVisualStates.Add(portal, state);
            }

            if (state.TollEffectActive == active)
            {
                return;
            }
            state.TollEffectActive = active;
            if (active)
            {
                AddActivePortal(portal, state);
                ApplyPortalPulseFrame(portal, state, GetPulseBlend(DateTime.UtcNow));
                return;
            }

            RemoveActivePortal(portal, state);
            RestorePortalState(portal, state);
        }

        private static void AddActivePortal(object portal, PortalVisualState state)
        {
            if (state.ListedAsActive)
            {
                return;
            }

            state.ListedAsActive = true;
            ActiveVisualPortals.Add(new WeakReference(portal));
        }

        private static void RemoveActivePortal(object portal, PortalVisualState state)
        {
            state.ListedAsActive = false;
            for (int index = ActiveVisualPortals.Count - 1; index >= 0; index--)
            {
                object candidate = ActiveVisualPortals[index].Target;
                if (candidate == null || ReferenceEquals(candidate, portal))
                {
                    ActiveVisualPortals.RemoveAt(index);
                }
            }
        }

        private static void RestorePortalState(object portal, PortalVisualState state)
        {
            if (state.TargetColorField != null && state.OriginalTargetColor != null)
            {
                state.TargetColorField.SetValue(portal, state.OriginalTargetColor);
            }
            foreach (ParticleVisualState particleState in state.Particles)
            {
                object mainModule = particleState.MainProperty.GetValue(particleState.Particle, null);
                particleState.StartColorProperty.SetValue(
                    mainModule,
                    particleState.OriginalStartColor,
                    null);
            }
            if (state.Light != null && state.LightColorProperty != null)
            {
                state.LightColorProperty.SetValue(state.Light, state.OriginalLightColor, null);
            }
            RestorePortalAudio(state);
        }

        private static void ApplyPortalPulseFrame(
            object portal,
            PortalVisualState state,
            float purpleBlend)
        {
            if (state.ColorConstructor != null)
            {
                object portalColor = CreateBlendedPulseColor(state, purpleBlend);
                float runeBlend = ConfiguredInverseRunePulse ? 1f - purpleBlend : purpleBlend;
                object runeColor = CreateBlendedPulseColor(
                    state,
                    runeBlend,
                    ConfiguredRuneBrightnessMultiplier);
                if (portalColor != null && runeColor != null)
                {
                    if (state.TargetColorField != null)
                    {
                        // Valheim uses m_colorTargetfound for the model emission, which is
                        // where the glowing frame symbols/runes are rendered.
                        state.TargetColorField.SetValue(portal, runeColor);
                    }
                    foreach (ParticleVisualState particleState in state.Particles)
                    {
                        object mainModule = particleState.MainProperty.GetValue(
                            particleState.Particle,
                            null);
                        object gradient = particleState.GradientConstructor.Invoke(new[] { portalColor });
                        particleState.StartColorProperty.SetValue(mainModule, gradient, null);
                    }
                    if (state.Light != null && state.LightColorProperty != null)
                    {
                        state.LightColorProperty.SetValue(state.Light, portalColor, null);
                    }
                }
            }
            ApplyPortalAudioPitch(portal, state, purpleBlend);
        }

        private static void ApplyPortalAudioPitch(
            object portal,
            PortalVisualState state,
            float purpleBlend)
        {
            if (!ConfiguredUnstableSound)
            {
                RestorePortalAudio(state);
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (state.AudioSources.Count == 0 && now >= state.NextAudioCaptureUtc)
            {
                state.NextAudioCaptureUtc = now + AudioCaptureRetryInterval;
                CapturePortalAudioSources(portal, state);
                if (state.AudioSources.Count > 0 && state.AudioMissingDiagnosticLogged &&
                    !state.AudioRecoveryLogged && _log != null)
                {
                    state.AudioRecoveryLogged = true;
                    _log.LogDebug("A late-created portal audio source was discovered; pitch wobble resumed.");
                }
            }

            float minimum = Math.Min(ConfiguredMinimumPitchMultiplier, ConfiguredMaximumPitchMultiplier);
            float maximum = Math.Max(ConfiguredMinimumPitchMultiplier, ConfiguredMaximumPitchMultiplier);
            float multiplier = minimum + (maximum - minimum) * purpleBlend;

            for (int index = state.AudioSources.Count - 1; index >= 0; index--)
            {
                AudioVisualState audioState = state.AudioSources[index];
                try
                {
                    audioState.PitchProperty.SetValue(
                        audioState.AudioSource,
                        audioState.OriginalPitch * multiplier,
                        null);
                    state.AudioPitchModified = true;
                }
                catch
                {
                    // Unity can destroy a child AudioSource before its portal wrapper is collected.
                    state.AudioSources.RemoveAt(index);
                }
            }

            if (state.AudioSources.Count == 0)
            {
                state.NextAudioCaptureUtc = now + AudioCaptureRetryInterval;
            }
        }

        private static void RestorePortalAudio(PortalVisualState state)
        {
            if (!state.AudioPitchModified)
            {
                return;
            }

            foreach (AudioVisualState audioState in state.AudioSources)
            {
                try
                {
                    audioState.PitchProperty.SetValue(
                        audioState.AudioSource,
                        audioState.OriginalPitch,
                        null);
                }
                catch
                {
                    // The portal or its audio child may already be shutting down.
                }
            }
            state.AudioPitchModified = false;
        }

        private static float GetPulseBlend(DateTime utcNow)
        {
            const double cycleSeconds = 2.6d;
            double seconds = utcNow.Ticks / (double)TimeSpan.TicksPerSecond;
            double phase = (seconds % cycleSeconds) / cycleSeconds;
            return (float)(0.5d - 0.5d * Math.Cos(phase * Math.PI * 2d));
        }

        private static object CreateBlendedPulseColor(
            PortalVisualState state,
            float purpleBlend,
            float brightnessMultiplier = 1f)
        {
            // The endpoints are deliberately somewhat dark. The glow remains visible, but
            // the slow color swing reads as strain rather than a celebratory rainbow effect.
            float orangeWeight = 1f - purpleBlend;
            return state.ColorConstructor.Invoke(new object[]
            {
                (1.00f * orangeWeight + 0.62f * purpleBlend) * brightnessMultiplier,
                (0.12f * orangeWeight + 0.06f * purpleBlend) * brightnessMultiplier,
                (0.015f * orangeWeight + 1.05f * purpleBlend) * brightnessMultiplier,
                1f
            });
        }

        private static PortalVisualState CapturePortalVisualState(object portal)
        {
            PortalVisualState state = new PortalVisualState();
            state.TargetColorField = FindField(portal.GetType(), "m_colorTargetfound");
            if (state.TargetColorField != null)
            {
                state.OriginalTargetColor = state.TargetColorField.GetValue(portal);
                state.ColorConstructor = state.TargetColorField.FieldType.GetConstructor(
                    new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            }

            object effectFade = ReadField(portal, "m_target_found");
            IEnumerable particles = effectFade == null ? null : ReadField(effectFade, "m_particles") as IEnumerable;
            if (particles != null && state.TargetColorField != null)
            {
                foreach (object particle in particles)
                {
                    if (particle == null)
                    {
                        continue;
                    }

                    PropertyInfo mainProperty = particle.GetType().GetProperty(
                        "main", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object mainModule = mainProperty == null ? null : mainProperty.GetValue(particle, null);
                    PropertyInfo startColorProperty = mainModule == null
                        ? null
                        : mainModule.GetType().GetProperty(
                            "startColor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (startColorProperty == null || !startColorProperty.CanRead || !startColorProperty.CanWrite)
                    {
                        continue;
                    }

                    ConstructorInfo gradientConstructor = startColorProperty.PropertyType.GetConstructor(
                        new[] { state.TargetColorField.FieldType });
                    if (gradientConstructor == null)
                    {
                        continue;
                    }

                    state.Particles.Add(new ParticleVisualState
                    {
                        Particle = particle,
                        MainProperty = mainProperty,
                        StartColorProperty = startColorProperty,
                        GradientConstructor = gradientConstructor,
                        OriginalStartColor = startColorProperty.GetValue(mainModule, null)
                    });
                }
            }

            state.Light = effectFade == null ? null : ReadField(effectFade, "m_light");
            if (state.Light != null)
            {
                state.LightColorProperty = state.Light.GetType().GetProperty(
                    "color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (state.LightColorProperty != null && state.LightColorProperty.CanRead &&
                    state.LightColorProperty.CanWrite)
                {
                    state.OriginalLightColor = state.LightColorProperty.GetValue(state.Light, null);
                }
                else
                {
                    state.LightColorProperty = null;
                }
            }

            CapturePortalAudioSources(portal, state);
            state.NextAudioCaptureUtc = DateTime.UtcNow + AudioCaptureRetryInterval;
            LogMissingPortalComponents(state);

            return state;
        }

        private static void LogMissingPortalComponents(PortalVisualState state)
        {
            if (state.DiagnosticsLogged)
            {
                return;
            }
            state.DiagnosticsLogged = true;

            List<string> missing = new List<string>();
            if (state.TargetColorField == null || state.ColorConstructor == null)
            {
                missing.Add("frame rune color");
            }
            if (state.Particles.Count == 0)
            {
                missing.Add("particle system");
            }
            if (state.Light == null || state.LightColorProperty == null)
            {
                missing.Add("portal light");
            }
            if (state.AudioSources.Count == 0)
            {
                missing.Add("audio source");
                state.AudioMissingDiagnosticLogged = true;
            }

            if (missing.Count > 0 && _log != null)
            {
                _log.LogDebug(
                    "Portal instability compatibility: could not find " +
                    string.Join(", ", missing.ToArray()) +
                    ". Available effects will continue; missing audio will be retried.");
            }
        }

        private static void CapturePortalAudioSources(object portal, PortalVisualState state)
        {
            if (_audioSourceType == null)
            {
                return;
            }

            MethodInfo getComponents = portal.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "GetComponentsInChildren" && !method.IsGenericMethod &&
                           parameters.Length == 2 && parameters[0].ParameterType == typeof(Type) &&
                           parameters[1].ParameterType == typeof(bool);
                });
            object[] arguments = { _audioSourceType, true };
            if (getComponents == null)
            {
                getComponents = portal.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return method.Name == "GetComponentsInChildren" && !method.IsGenericMethod &&
                               parameters.Length == 1 && parameters[0].ParameterType == typeof(Type);
                    });
                arguments = new object[] { _audioSourceType };
            }
            if (getComponents == null)
            {
                return;
            }

            IEnumerable audioSources = getComponents.Invoke(portal, arguments) as IEnumerable;
            if (audioSources == null)
            {
                return;
            }

            foreach (object audioSource in audioSources)
            {
                if (audioSource == null)
                {
                    continue;
                }

                PropertyInfo pitchProperty = audioSource.GetType().GetProperty(
                    "pitch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pitchProperty == null || !pitchProperty.CanRead || !pitchProperty.CanWrite)
                {
                    continue;
                }

                state.AudioSources.Add(new AudioVisualState
                {
                    AudioSource = audioSource,
                    PitchProperty = pitchProperty,
                    OriginalPitch = Convert.ToSingle(pitchProperty.GetValue(audioSource, null))
                });
            }
        }

        internal static void RestorePortalVisuals()
        {
            object[] portals = ActiveVisualPortals
                .Select(reference => reference.Target)
                .Where(portal => portal != null)
                .ToArray();
            ActiveVisualPortals.Clear();

            foreach (object portal in portals)
            {
                try
                {
                    PortalVisualState state;
                    if (!PortalVisualStates.TryGetValue(portal, out state))
                    {
                        continue;
                    }
                    state.ListedAsActive = false;
                    state.TollEffectActive = false;
                    RestorePortalState(portal, state);
                }
                catch
                {
                    // A portal may be in the middle of being destroyed during shutdown.
                }
            }
        }

        private static int ConfiguredCoreCost
        {
            get
            {
                return Math.Max(0, UnstablePortalsMetalTransportPlugin.SurtlingCoreCost == null
                    ? 1
                    : UnstablePortalsMetalTransportPlugin.SurtlingCoreCost.Value);
            }
        }

        private static int ConfiguredEyeCost
        {
            get
            {
                return Math.Max(0, UnstablePortalsMetalTransportPlugin.GreydwarfEyeCost == null
                    ? 5
                    : UnstablePortalsMetalTransportPlugin.GreydwarfEyeCost.Value);
            }
        }

        private static float ConfiguredDamagePercent
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.PortalDamagePercent == null
                    ? 10f
                    : UnstablePortalsMetalTransportPlugin.PortalDamagePercent.Value;
                return Math.Max(0f, Math.Min(100f, value));
            }
        }

        private static bool ConfiguredDamageSource
        {
            get
            {
                return UnstablePortalsMetalTransportPlugin.DamageSourcePortal == null ||
                       UnstablePortalsMetalTransportPlugin.DamageSourcePortal.Value;
            }
        }

        private static bool ConfiguredDamageDestination
        {
            get
            {
                return UnstablePortalsMetalTransportPlugin.DamageDestinationPortal == null ||
                       UnstablePortalsMetalTransportPlugin.DamageDestinationPortal.Value;
            }
        }

        private static bool ConfiguredPreventSourcePortalDestruction
        {
            get
            {
                return UnstablePortalsMetalTransportPlugin.PreventSourcePortalDestruction != null &&
                       UnstablePortalsMetalTransportPlugin.PreventSourcePortalDestruction.Value;
            }
        }

        private static float ConfiguredPlayerDamageMaxHealthPercent
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.PlayerDamageMaxHealthPercent == null
                    ? 0f
                    : UnstablePortalsMetalTransportPlugin.PlayerDamageMaxHealthPercent.Value;
                return Math.Max(0f, Math.Min(100f, value));
            }
        }

        private static float ConfiguredPlayerDamageCurrentHealthPercent
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.PlayerDamageCurrentHealthPercent == null
                    ? 0f
                    : UnstablePortalsMetalTransportPlugin.PlayerDamageCurrentHealthPercent.Value;
                return Math.Max(0f, Math.Min(100f, value));
            }
        }

        private static float ConfiguredPlayerDamageFlat
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.PlayerDamageFlat == null
                    ? 0f
                    : UnstablePortalsMetalTransportPlugin.PlayerDamageFlat.Value;
                return Math.Max(0f, Math.Min(10000f, value));
            }
        }

        private static bool ConfiguredInverseRunePulse
        {
            get
            {
                return UnstablePortalsMetalTransportPlugin.InverseRunePulse == null ||
                       UnstablePortalsMetalTransportPlugin.InverseRunePulse.Value;
            }
        }

        private static float ConfiguredRuneBrightnessMultiplier
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.RuneBrightnessMultiplier == null
                    ? 3f
                    : UnstablePortalsMetalTransportPlugin.RuneBrightnessMultiplier.Value;
                return Math.Max(0.5f, Math.Min(10f, value));
            }
        }

        private static bool ConfiguredUnstableSound
        {
            get
            {
                return UnstablePortalsMetalTransportPlugin.EnableUnstableSound == null ||
                       UnstablePortalsMetalTransportPlugin.EnableUnstableSound.Value;
            }
        }

        private static float ConfiguredMinimumPitchMultiplier
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.MinimumPitchMultiplier == null
                    ? 0.82f
                    : UnstablePortalsMetalTransportPlugin.MinimumPitchMultiplier.Value;
                return Math.Max(0.5f, Math.Min(1.5f, value));
            }
        }

        private static float ConfiguredMaximumPitchMultiplier
        {
            get
            {
                float value = UnstablePortalsMetalTransportPlugin.MaximumPitchMultiplier == null
                    ? 0.96f
                    : UnstablePortalsMetalTransportPlugin.MaximumPitchMultiplier.Value;
                return Math.Max(0.5f, Math.Min(1.5f, value));
            }
        }

        private static bool HasRequiredToll(InventorySnapshot snapshot)
        {
            return snapshot.CoreCount >= ConfiguredCoreCost && snapshot.EyeCount >= ConfiguredEyeCost;
        }

        private static string DescribeToll()
        {
            List<string> parts = new List<string>();
            if (ConfiguredCoreCost > 0)
            {
                parts.Add(ConfiguredCoreCost + " Surtling " +
                    (ConfiguredCoreCost == 1 ? "Core" : "Cores"));
            }
            if (ConfiguredEyeCost > 0)
            {
                parts.Add(ConfiguredEyeCost + " Greydwarf " +
                    (ConfiguredEyeCost == 1 ? "Eye" : "Eyes"));
            }
            return parts.Count == 0 ? "no items" : string.Join(" and ", parts.ToArray());
        }

        private static object GetConnectedPortalId(object portal)
        {
            try
            {
                object nview = ReadField(portal, "m_nview");
                if (nview == null)
                {
                    return null;
                }

                MethodInfo getZdo = nview.GetType().GetMethod(
                    "GetZDO", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                object zdo = getZdo == null ? null : getZdo.Invoke(nview, null);
                if (zdo == null)
                {
                    return null;
                }

                MethodInfo getConnection = zdo.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return method.Name == "GetConnectionZDOID" && parameters.Length == 1 &&
                               parameters[0].ParameterType.IsEnum;
                    });
                if (getConnection == null)
                {
                    return null;
                }

                Type connectionType = getConnection.GetParameters()[0].ParameterType;
                object portalConnection = Enum.Parse(connectionType, "Portal", true);
                object destinationId = getConnection.Invoke(zdo, new[] { portalConnection });
                return IsNoneZdoId(destinationId) ? null : destinationId;
            }
            catch (Exception exception)
            {
                _log.LogDebug("Could not resolve the destination portal: " + exception.Message);
                return null;
            }
        }

        private static bool IsNoneZdoId(object zdoId)
        {
            if (zdoId == null)
            {
                return true;
            }
            MethodInfo isNone = zdoId.GetType().GetMethod(
                "IsNone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            return isNone != null && Convert.ToBoolean(isNone.Invoke(zdoId, null));
        }

        private static void QueueDestinationDamage(object destinationPortalId, float damagePercent)
        {
            PendingDestinationDamage.Add(new PendingPortalDamage
            {
                ExpiresUtc = DateTime.UtcNow + PendingDamageLifetime,
                DestinationPortalId = destinationPortalId,
                DamagePercent = damagePercent
            });
            _nextDamagePollUtc = DateTime.MinValue;
        }

        internal static void UpdatePendingPortalDamage()
        {
            if (PendingDestinationDamage.Count == 0 || DateTime.UtcNow < _nextDamagePollUtc)
            {
                return;
            }

            _nextDamagePollUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            for (int index = PendingDestinationDamage.Count - 1; index >= 0; index--)
            {
                PendingPortalDamage pending = PendingDestinationDamage[index];
                if (pending.ExpiresUtc < DateTime.UtcNow)
                {
                    _log.LogWarning("Destination portal did not load in time; its toll damage was not applied.");
                    PendingDestinationDamage.RemoveAt(index);
                    continue;
                }

                object destination = FindLoadedZNetObject(pending.DestinationPortalId);
                if (destination != null && ApplyPortalDamage(destination, pending.DamagePercent))
                {
                    PendingDestinationDamage.RemoveAt(index);
                }
            }
        }

        internal static void ClearPendingPortalDamage()
        {
            PendingDestinationDamage.Clear();
            _nextDamagePollUtc = DateTime.MinValue;
        }

        private static object FindLoadedZNetObject(object zdoId)
        {
            try
            {
                if (_zNetSceneType == null || zdoId == null)
                {
                    return null;
                }

                FieldInfo instanceField = _zNetSceneType.GetField(
                    "instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo instanceProperty = _zNetSceneType.GetProperty(
                    "instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object scene = instanceField != null
                    ? instanceField.GetValue(null)
                    : (instanceProperty == null ? null : instanceProperty.GetValue(null, null));
                if (scene == null)
                {
                    return null;
                }

                MethodInfo findInstance = _zNetSceneType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return method.Name == "FindInstance" && parameters.Length == 1 &&
                               parameters[0].ParameterType.IsInstanceOfType(zdoId);
                    });
                return findInstance == null ? null : findInstance.Invoke(scene, new[] { zdoId });
            }
            catch (Exception exception)
            {
                _log.LogDebug("Could not look up the destination portal: " + exception.Message);
                return null;
            }
        }

        private static bool ApplyPortalDamage(object portalOrGameObject, float damagePercent)
        {
            try
            {
                object wearNTear = FindComponent(portalOrGameObject, _wearNTearType);
                if (wearNTear == null || _hitDataType == null)
                {
                    return false;
                }

                object maximumHealthValue = ReadField(wearNTear, "m_health");
                float maximumHealth = maximumHealthValue == null
                    ? 0f
                    : Convert.ToSingle(maximumHealthValue);
                float damage = maximumHealth * damagePercent / 100f;
                if (damage <= 0f)
                {
                    return true;
                }

                object hit = CreateRawDamageHit(damage);
                if (hit == null)
                {
                    return false;
                }

                SetHitPoint(hit, wearNTear);
                SetEnumField(hit, "m_hitType", "Structural");

                MethodInfo damageMethod = wearNTear.GetType().GetMethod(
                    "Damage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { _hitDataType }, null);
                if (damageMethod == null)
                {
                    return false;
                }
                damageMethod.Invoke(wearNTear, new[] { hit });
                return true;
            }
            catch (Exception exception)
            {
                _log.LogWarning("Could not damage a toll portal: " + exception.Message);
                return false;
            }
        }

        private static bool WouldDestroySourcePortal(object sourcePortal)
        {
            if (!ConfiguredPreventSourcePortalDestruction || !ConfiguredDamageSource ||
                ConfiguredDamagePercent <= 0f)
            {
                return false;
            }

            try
            {
                object wearNTear = FindComponent(sourcePortal, _wearNTearType);
                float currentHealth;
                float maximumHealth;
                if (wearNTear == null ||
                    !TryReadPortalHealth(wearNTear, out currentHealth, out maximumHealth))
                {
                    _log.LogDebug(
                        "Source-portal destruction prevention could not read portal health; travel was not blocked.");
                    return false;
                }

                float damage = maximumHealth * ConfiguredDamagePercent / 100f;
                return damage > 0f && damage >= currentHealth;
            }
            catch (Exception exception)
            {
                _log.LogDebug(
                    "Source-portal destruction prevention could not inspect the portal: " +
                    exception.Message);
                return false;
            }
        }

        private static bool TryReadPortalHealth(
            object wearNTear,
            out float currentHealth,
            out float maximumHealth)
        {
            currentHealth = 0f;
            maximumHealth = 0f;

            object maximumHealthValue = ReadField(wearNTear, "m_health");
            if (maximumHealthValue == null)
            {
                return false;
            }
            maximumHealth = Convert.ToSingle(maximumHealthValue);
            if (maximumHealth <= 0f)
            {
                return false;
            }

            if (TryInvokeFloatMethod(wearNTear, "GetHealth", out currentHealth))
            {
                return true;
            }

            float healthPercentage;
            if (TryInvokeFloatMethod(wearNTear, "GetHealthPercentage", out healthPercentage))
            {
                currentHealth = maximumHealth * healthPercentage;
                return true;
            }

            PropertyInfo currentHealthProperty = wearNTear.GetType().GetProperty(
                "CurrentHealth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (currentHealthProperty != null && currentHealthProperty.CanRead)
            {
                currentHealth = Convert.ToSingle(currentHealthProperty.GetValue(wearNTear, null));
                return true;
            }

            FieldInfo currentHealthField = FindField(wearNTear.GetType(), "CurrentHealth");
            if (currentHealthField != null)
            {
                currentHealth = Convert.ToSingle(currentHealthField.GetValue(wearNTear));
                return true;
            }

            return TryReadWearNTearZdoHealth(wearNTear, maximumHealth, out currentHealth);
        }

        private static bool TryReadWearNTearZdoHealth(
            object wearNTear,
            float maximumHealth,
            out float currentHealth)
        {
            currentHealth = 0f;
            object nview = ReadField(wearNTear, "m_nview");
            if (nview == null)
            {
                return false;
            }

            MethodInfo getZdo = nview.GetType().GetMethod(
                "GetZDO", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            object zdo = getZdo == null ? null : getZdo.Invoke(nview, null);
            if (zdo == null)
            {
                return false;
            }

            Type zdoVarsType = AccessTools.TypeByName("ZDOVars");
            FieldInfo healthHashField = zdoVarsType == null
                ? null
                : zdoVarsType.GetField(
                    "s_health", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object healthKey = healthHashField == null ? null : healthHashField.GetValue(null);

            MethodInfo getFloat = zdo.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return method.Name == "GetFloat" && parameters.Length == 2 &&
                           parameters[1].ParameterType == typeof(float) &&
                           ((healthKey != null && parameters[0].ParameterType.IsInstanceOfType(healthKey)) ||
                            parameters[0].ParameterType == typeof(string));
                });
            if (getFloat == null)
            {
                return false;
            }

            object key = getFloat.GetParameters()[0].ParameterType == typeof(string)
                ? (object)"health"
                : healthKey;
            if (key == null)
            {
                return false;
            }

            currentHealth = Convert.ToSingle(getFloat.Invoke(zdo, new[] { key, (object)maximumHealth }));
            return true;
        }

        private static void ApplyPlayerDamage(object player)
        {
            float maxHealthPercent = ConfiguredPlayerDamageMaxHealthPercent;
            float currentHealthPercent = ConfiguredPlayerDamageCurrentHealthPercent;
            float flatDamage = ConfiguredPlayerDamageFlat;
            if (maxHealthPercent <= 0f && currentHealthPercent <= 0f && flatDamage <= 0f)
            {
                return;
            }

            try
            {
                float currentHealth;
                float maximumHealth;
                if (!TryInvokeFloatMethod(player, "GetHealth", out currentHealth) ||
                    !TryInvokeFloatMethod(player, "GetMaxHealth", out maximumHealth))
                {
                    _log.LogWarning("Could not read player health, so portal player damage was not applied.");
                    return;
                }

                float damage = maximumHealth * maxHealthPercent / 100f +
                               currentHealth * currentHealthPercent / 100f +
                               flatDamage;
                if (damage <= 0f)
                {
                    return;
                }

                object hit = CreateRawDamageHit(damage);
                if (hit == null)
                {
                    _log.LogWarning("Could not create player portal damage data.");
                    return;
                }

                SetHitPoint(hit, player);
                SetBoolFieldIfPresent(hit, "m_blockable", false);
                SetBoolFieldIfPresent(hit, "m_dodgeable", false);
                SetBoolFieldIfPresent(hit, "m_ignorePVP", true);

                MethodInfo damageMethod = player.GetType().GetMethod(
                    "Damage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { _hitDataType }, null);
                if (damageMethod == null)
                {
                    _log.LogWarning("Could not find the player damage method.");
                    return;
                }

                damageMethod.Invoke(player, new[] { hit });
                _log.LogInfo(
                    "Applied " + damage.ToString("0.##") +
                    " configured player damage after restricted-item portal travel.");
            }
            catch (Exception exception)
            {
                _log.LogWarning("Could not damage the player after portal travel: " + exception.Message);
            }
        }

        private static object CreateRawDamageHit(float damage)
        {
            if (_hitDataType == null)
            {
                return null;
            }

            ConstructorInfo hitConstructor = _hitDataType.GetConstructor(new[] { typeof(float) });
            object hit = hitConstructor == null
                ? Activator.CreateInstance(_hitDataType)
                : hitConstructor.Invoke(new object[] { damage });
            if (hitConstructor != null)
            {
                return hit;
            }

            object damageTypes = ReadField(hit, "m_damage");
            FieldInfo rawDamage = damageTypes == null
                ? null
                : FindField(damageTypes.GetType(), "m_damage");
            FieldInfo hitDamage = FindField(hit.GetType(), "m_damage");
            if (rawDamage == null || hitDamage == null)
            {
                return null;
            }

            rawDamage.SetValue(damageTypes, damage);
            hitDamage.SetValue(hit, damageTypes);
            return hit;
        }

        private static bool TryInvokeFloatMethod(object instance, string methodName, out float value)
        {
            value = 0f;
            if (instance == null)
            {
                return false;
            }

            MethodInfo method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
            {
                return false;
            }

            object result = method.Invoke(instance, null);
            if (result == null)
            {
                return false;
            }

            value = Convert.ToSingle(result);
            return true;
        }

        private static void SetBoolFieldIfPresent(object instance, string fieldName, bool value)
        {
            FieldInfo field = FindField(instance.GetType(), fieldName);
            if (field != null && field.FieldType == typeof(bool))
            {
                field.SetValue(instance, value);
            }
        }

        private static object FindComponent(object instance, Type componentType)
        {
            if (instance == null || componentType == null)
            {
                return null;
            }
            if (componentType.IsInstanceOfType(instance))
            {
                return instance;
            }

            MethodInfo getComponent = instance.GetType().GetMethod(
                "GetComponent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Type) }, null);
            object component = getComponent == null
                ? null
                : getComponent.Invoke(instance, new object[] { componentType });
            if (component != null)
            {
                return component;
            }

            PropertyInfo gameObjectProperty = instance.GetType().GetProperty(
                "gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object gameObject = gameObjectProperty == null ? null : gameObjectProperty.GetValue(instance, null);
            if (gameObject == null || ReferenceEquals(gameObject, instance))
            {
                return null;
            }
            return FindComponent(gameObject, componentType);
        }

        private static void SetHitPoint(object hit, object component)
        {
            FieldInfo pointField = FindField(hit.GetType(), "m_point");
            PropertyInfo transformProperty = component.GetType().GetProperty(
                "transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object transform = transformProperty == null ? null : transformProperty.GetValue(component, null);
            PropertyInfo positionProperty = transform == null
                ? null
                : transform.GetType().GetProperty(
                    "position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object position = positionProperty == null ? null : positionProperty.GetValue(transform, null);
            if (pointField != null && position != null && pointField.FieldType.IsInstanceOfType(position))
            {
                pointField.SetValue(hit, position);
            }
        }

        private static void SetEnumField(object instance, string fieldName, string value)
        {
            FieldInfo field = FindField(instance.GetType(), fieldName);
            if (field != null && field.FieldType.IsEnum)
            {
                field.SetValue(instance, Enum.Parse(field.FieldType, value, true));
            }
        }

        private static InventorySnapshot ReadInventory(object humanoid)
        {
            try
            {
                MethodInfo getInventory = humanoid.GetType().GetMethod(
                    "GetInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object inventory = getInventory == null ? null : getInventory.Invoke(humanoid, null);
                if (inventory == null)
                {
                    return null;
                }

                IEnumerable items = GetAllItems(inventory);
                if (items == null)
                {
                    return null;
                }

                InventorySnapshot snapshot = new InventorySnapshot { Inventory = inventory };
                foreach (object item in items)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    int stack = ReadIntField(item, "m_stack", 1);
                    if (stack <= 0)
                    {
                        continue;
                    }

                    if (IsSurtlingCore(item))
                    {
                        snapshot.CoreCount += stack;
                    }
                    if (IsGreydwarfEye(item))
                    {
                        snapshot.EyeCount += stack;
                    }

                    object shared = ReadField(item, "m_shared");
                    if (shared == null)
                    {
                        continue;
                    }

                    int toolTier = ReadIntField(shared, "m_toolTier", 0);
                    if (toolTier >= 1000)
                    {
                        snapshot.HasAbsoluteRestriction = true;
                    }
                    else if (!ReadBoolField(shared, "m_teleportable"))
                    {
                        snapshot.HasEligibleRestrictedItem = true;
                    }
                }
                return snapshot;
            }
            catch (Exception exception)
            {
                _log.LogWarning("Could not inspect player inventory: " + exception.Message);
                return null;
            }
        }

        private static IEnumerable GetAllItems(object inventory)
        {
            MethodInfo getAllItems = inventory.GetType().GetMethod(
                "GetAllItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            return getAllItems == null ? null : getAllItems.Invoke(inventory, null) as IEnumerable;
        }

        private static bool ConsumeToll(object inventory)
        {
            try
            {
                IEnumerable items = GetAllItems(inventory);
                if (items == null)
                {
                    return false;
                }

                List<object> itemList = items.Cast<object>().Where(item => item != null).ToList();
                int coreCost = ConfiguredCoreCost;
                int eyeCost = ConfiguredEyeCost;
                if (CountItems(itemList, IsSurtlingCore) < coreCost ||
                    CountItems(itemList, IsGreydwarfEye) < eyeCost)
                {
                    return false;
                }

                MethodInfo removeItem = inventory.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "RemoveItem")
                        {
                            return false;
                        }
                        ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length == 1 &&
                               itemList.Any(item => parameters[0].ParameterType.IsInstanceOfType(item));
                    });
                if ((coreCost > 0 || eyeCost > 0) && removeItem == null)
                {
                    return false;
                }

                if (!ConsumeItemAmount(inventory, itemList, IsSurtlingCore, coreCost, removeItem) ||
                    !ConsumeItemAmount(inventory, itemList, IsGreydwarfEye, eyeCost, removeItem))
                {
                    return false;
                }

                MethodInfo changed = inventory.GetType().GetMethod(
                    "Changed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (changed != null)
                {
                    changed.Invoke(inventory, null);
                }
                return true;
            }
            catch (Exception exception)
            {
                _log.LogWarning("Could not consume the configured portal toll: " + exception.Message);
                return false;
            }
        }

        private static int CountItems(IEnumerable<object> items, Func<object, bool> predicate)
        {
            return items.Where(predicate).Sum(item => Math.Max(0, ReadIntField(item, "m_stack", 1)));
        }

        private static bool ConsumeItemAmount(
            object inventory,
            IEnumerable<object> items,
            Func<object, bool> predicate,
            int amount,
            MethodInfo removeItem)
        {
            int remaining = amount;
            foreach (object item in items.Where(predicate).ToList())
            {
                if (remaining <= 0)
                {
                    break;
                }

                FieldInfo stackField = FindField(item.GetType(), "m_stack");
                if (stackField == null)
                {
                    return false;
                }

                int stack = Math.Max(0, Convert.ToInt32(stackField.GetValue(item)));
                if (stack <= remaining)
                {
                    if (removeItem == null ||
                        !removeItem.GetParameters()[0].ParameterType.IsInstanceOfType(item))
                    {
                        return false;
                    }
                    removeItem.Invoke(inventory, new[] { item });
                    remaining -= stack;
                }
                else
                {
                    stackField.SetValue(item, stack - remaining);
                    remaining = 0;
                }
            }
            return remaining == 0;
        }

        private static bool IsSurtlingCore(object item)
        {
            object dropPrefab = ReadField(item, "m_dropPrefab");
            string prefabName = ReadName(dropPrefab);
            if (string.Equals(prefabName, CorePrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            object shared = ReadField(item, "m_shared");
            string sharedName = (shared == null ? null : ReadField(shared, "m_name")) as string;
            return string.Equals(sharedName, "$item_surtlingcore", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sharedName, "Surtling core", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGreydwarfEye(object item)
        {
            object dropPrefab = ReadField(item, "m_dropPrefab");
            string prefabName = ReadName(dropPrefab);
            if (string.Equals(prefabName, EyePrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            object shared = ReadField(item, "m_shared");
            string sharedName = (shared == null ? null : ReadField(shared, "m_name")) as string;
            return string.Equals(sharedName, "$item_greydwarfeye", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sharedName, "$item_greydwarf_eye", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sharedName, "Greydwarf eye", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadName(object value)
        {
            if (value == null)
            {
                return null;
            }
            PropertyInfo nameProperty = value.GetType().GetProperty(
                "name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return nameProperty == null ? null : nameProperty.GetValue(value, null) as string;
        }

        private static object ReadField(object instance, string fieldName)
        {
            if (instance == null)
            {
                return null;
            }
            FieldInfo field = FindField(instance.GetType(), fieldName);
            return field == null ? null : field.GetValue(instance);
        }

        private static bool ReadBoolField(object instance, string fieldName)
        {
            object value = ReadField(instance, fieldName);
            return value is bool && (bool)value;
        }

        private static int ReadIntField(object instance, string fieldName, int fallback)
        {
            object value = ReadField(instance, fieldName);
            return value == null ? fallback : Convert.ToInt32(value);
        }

        private static FieldInfo FindField(Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
                type = type.BaseType;
            }
            return null;
        }

        private static void ShowCenterMessage(object player, string text)
        {
            try
            {
                MethodInfo messageMethod = player.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == "Message")
                    .FirstOrDefault(method =>
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length >= 2 && parameters[0].ParameterType.IsEnum &&
                               parameters[1].ParameterType == typeof(string);
                    });
                if (messageMethod == null)
                {
                    return;
                }

                ParameterInfo[] parameters = messageMethod.GetParameters();
                object[] arguments = new object[parameters.Length];
                arguments[0] = Enum.Parse(parameters[0].ParameterType, "Center", true);
                arguments[1] = text;
                for (int index = 2; index < parameters.Length; index++)
                {
                    arguments[index] = parameters[index].HasDefaultValue
                        ? parameters[index].DefaultValue
                        : (parameters[index].ParameterType.IsValueType
                            ? Activator.CreateInstance(parameters[index].ParameterType)
                            : null);
                }
                messageMethod.Invoke(player, arguments);
            }
            catch (Exception exception)
            {
                _log.LogDebug("Could not show portal message: " + exception.Message);
            }
        }

        private static void SetPendingTrip(
            object player,
            object inventory,
            object sourcePortal,
            object destinationPortalId)
        {
            RemovePendingTrip(player);
            PendingTrips.Add(player, new PendingTrip
            {
                ExpiresUtc = DateTime.UtcNow + PendingTripLifetime,
                Inventory = inventory,
                SourcePortal = sourcePortal,
                DestinationPortalId = destinationPortalId
            });
        }

        private static PendingTrip GetPendingTrip(object player)
        {
            PendingTrip trip;
            if (!PendingTrips.TryGetValue(player, out trip))
            {
                return null;
            }
            if (trip.ExpiresUtc >= DateTime.UtcNow)
            {
                return trip;
            }
            RemovePendingTrip(player);
            return null;
        }

        private static bool HasPendingTrip(object player)
        {
            return GetPendingTrip(player) != null;
        }

        private static void RemovePendingTrip(object player)
        {
            if (player != null)
            {
                PendingTrips.Remove(player);
            }
        }
    }
}
