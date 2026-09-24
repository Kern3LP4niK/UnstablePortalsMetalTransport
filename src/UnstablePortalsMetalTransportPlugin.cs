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
        public const string PluginVersion = "1.2.2";

        private Harmony _harmony;

        internal static ConfigEntry<int> SurtlingCoreCost { get; private set; }
        internal static ConfigEntry<int> GreydwarfEyeCost { get; private set; }
        internal static ConfigEntry<float> PortalDamagePercent { get; private set; }
        internal static ConfigEntry<bool> DamageSourcePortal { get; private set; }
        internal static ConfigEntry<bool> DamageDestinationPortal { get; private set; }
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
        private static readonly ConditionalWeakTable<object, PendingTrip> PendingTrips =
            new ConditionalWeakTable<object, PendingTrip>();
        private static readonly ConditionalWeakTable<object, PortalVisualState> PortalVisualStates =
            new ConditionalWeakTable<object, PortalVisualState>();
        private static readonly List<WeakReference> VisualPortals = new List<WeakReference>();
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

            SetPendingTrip(
                player,
                snapshot.Inventory,
                __instance,
                GetConnectedPortalId(__instance));
        }

        private static void PortalTeleportPostfix(object[] __args)
        {
            object player = FindPlayer(__args);
            if (player != null && HasPendingTrip(player))
            {
                // A successful Player.TeleportTo postfix removes the trip first. If it is
                // still present here, Valheim rejected or did not start the teleport.
                RemovePendingTrip(player);
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
            if (VisualPortals.Count == 0)
            {
                return;
            }

            float purpleBlend = GetPulseBlend(DateTime.UtcNow);
            for (int index = VisualPortals.Count - 1; index >= 0; index--)
            {
                object portal = VisualPortals[index].Target;
                if (portal == null)
                {
                    VisualPortals.RemoveAt(index);
                    continue;
                }

                PortalVisualState state;
                if (!PortalVisualStates.TryGetValue(portal, out state) || !state.TollEffectActive)
                {
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
                    VisualPortals.RemoveAt(index);
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
                state = CapturePortalVisualState(portal);
                PortalVisualStates.Add(portal, state);
                VisualPortals.Add(new WeakReference(portal));
            }

            if (state.TollEffectActive == active)
            {
                return;
            }
            if (active && state.ColorConstructor == null)
            {
                return;
            }

            state.TollEffectActive = active;
            if (active)
            {
                ApplyPortalPulseFrame(portal, state, GetPulseBlend(DateTime.UtcNow));
                return;
            }

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
            if (state.ColorConstructor == null)
            {
                return;
            }
            object portalColor = CreateBlendedPulseColor(state, purpleBlend);
            float runeBlend = ConfiguredInverseRunePulse ? 1f - purpleBlend : purpleBlend;
            object runeColor = CreateBlendedPulseColor(
                state,
                runeBlend,
                ConfiguredRuneBrightnessMultiplier);
            if (portalColor == null || runeColor == null)
            {
                return;
            }

            if (state.TargetColorField != null)
            {
                // Valheim uses m_colorTargetfound for the model emission, which is
                // where the glowing frame symbols/runes are rendered.
                state.TargetColorField.SetValue(portal, runeColor);
            }
            foreach (ParticleVisualState particleState in state.Particles)
            {
                object mainModule = particleState.MainProperty.GetValue(particleState.Particle, null);
                object gradient = particleState.GradientConstructor.Invoke(new[] { portalColor });
                particleState.StartColorProperty.SetValue(mainModule, gradient, null);
            }
            if (state.Light != null && state.LightColorProperty != null)
            {
                state.LightColorProperty.SetValue(state.Light, portalColor, null);
            }
            ApplyPortalAudioPitch(state, purpleBlend);
        }

        private static void ApplyPortalAudioPitch(PortalVisualState state, float purpleBlend)
        {
            float minimum = Math.Min(ConfiguredMinimumPitchMultiplier, ConfiguredMaximumPitchMultiplier);
            float maximum = Math.Max(ConfiguredMinimumPitchMultiplier, ConfiguredMaximumPitchMultiplier);
            float multiplier = ConfiguredUnstableSound
                ? minimum + (maximum - minimum) * purpleBlend
                : 1f;

            foreach (AudioVisualState audioState in state.AudioSources)
            {
                try
                {
                    audioState.PitchProperty.SetValue(
                        audioState.AudioSource,
                        audioState.OriginalPitch * multiplier,
                        null);
                }
                catch
                {
                    // Unity can destroy a child AudioSource before its portal wrapper is collected.
                }
            }
        }

        private static void RestorePortalAudio(PortalVisualState state)
        {
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
            if (state.TargetColorField == null)
            {
                return state;
            }

            state.OriginalTargetColor = state.TargetColorField.GetValue(portal);
            state.ColorConstructor = state.TargetColorField.FieldType.GetConstructor(
                new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            if (state.ColorConstructor == null)
            {
                return state;
            }

            object effectFade = ReadField(portal, "m_target_found");
            IEnumerable particles = effectFade == null ? null : ReadField(effectFade, "m_particles") as IEnumerable;
            if (particles != null)
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

            return state;
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
            foreach (WeakReference reference in VisualPortals)
            {
                object portal = reference.Target;
                if (portal == null)
                {
                    continue;
                }

                try
                {
                    SetPortalTollEffect(portal, false);
                }
                catch
                {
                    // A portal may be in the middle of being destroyed during shutdown.
                }
            }
            VisualPortals.Clear();
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

                ConstructorInfo hitConstructor = _hitDataType.GetConstructor(new[] { typeof(float) });
                object hit = hitConstructor == null
                    ? Activator.CreateInstance(_hitDataType)
                    : hitConstructor.Invoke(new object[] { damage });
                if (hitConstructor == null)
                {
                    object damageTypes = ReadField(hit, "m_damage");
                    FieldInfo rawDamage = damageTypes == null
                        ? null
                        : FindField(damageTypes.GetType(), "m_damage");
                    if (rawDamage == null)
                    {
                        return false;
                    }
                    rawDamage.SetValue(damageTypes, damage);
                    FindField(hit.GetType(), "m_damage").SetValue(hit, damageTypes);
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
