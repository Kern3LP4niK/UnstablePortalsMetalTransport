using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;

public sealed class SharedData
{
    public bool m_teleportable;
    public string m_name;
    public int m_toolTier;
}

public sealed class DropPrefab
{
    public string name { get; set; }
}

public sealed class ItemData
{
    public int m_stack;
    public SharedData m_shared;
    public DropPrefab m_dropPrefab;
}

public sealed class Inventory
{
    private readonly List<ItemData> _items = new List<ItemData>();
    public List<ItemData> GetAllItems() { return _items; }
    public void Add(ItemData item) { _items.Add(item); }
    public void RemoveItem(ItemData item) { _items.Remove(item); }
    public void Changed() { }

    public bool IsTeleportable(bool allowAllItems)
    {
        if (_items.Any(item => item.m_shared.m_toolTier >= 1000)) { return false; }
        return allowAllItems || _items.All(item => item.m_shared.m_teleportable);
    }
}

public class Humanoid
{
    protected readonly Inventory Inventory = new Inventory();
    public Inventory GetInventory() { return Inventory; }
    public bool IsTeleportable(bool allowAllItems) { return Inventory.IsTeleportable(allowAllItems); }
}

public enum MessageType
{
    TopLeft,
    Center
}

public sealed class Player : Humanoid
{
    public static Player m_localPlayer;
    public bool TeleportStarted { get; private set; }
    public bool RejectTeleport { get; set; }
    public readonly List<string> Messages = new List<string>();

    public bool TeleportTo(int position, int rotation, bool distantTeleport)
    {
        if (RejectTeleport) { return false; }
        TeleportStarted = true;
        return true;
    }

    public void Message(MessageType type, string text, int amount = 0, object icon = null)
    {
        Messages.Add(text);
    }
}

public struct MockColor
{
    public float R;
    public float G;
    public float B;
    public float A;

    public MockColor(float red, float green, float blue, float alpha)
    {
        R = red;
        G = green;
        B = blue;
        A = alpha;
    }
}

public struct MockGradient
{
    public MockColor Color;
    public MockGradient(MockColor color) { Color = color; }
}

public sealed class MockParticle
{
    public MockGradient StartColor;
    public MockParticle(MockColor color) { StartColor = new MockGradient(color); }
    public MockMainModule main { get { return new MockMainModule(this); } }
}

public struct MockMainModule
{
    private readonly MockParticle _particle;
    public MockMainModule(MockParticle particle) { _particle = particle; }
    public MockGradient startColor
    {
        get { return _particle.StartColor; }
        set { _particle.StartColor = value; }
    }
}

public sealed class MockLight
{
    public MockColor color { get; set; }
}

public sealed class MockAudioSource
{
    public float pitch { get; set; } = 1f;
}

public sealed class MockEffectFade
{
    private readonly MockParticle[] m_particles;
    private readonly MockLight m_light;

    public MockEffectFade(MockColor color)
    {
        m_particles = new[] { new MockParticle(color) };
        m_light = new MockLight { color = color };
    }

    public MockParticle Particle { get { return m_particles[0]; } }
    public MockLight Light { get { return m_light; } }
}

public sealed class HitData
{
    public struct DamageTypes
    {
        public float m_damage;
    }

    public enum HitType
    {
        Undefined,
        Structural
    }

    public DamageTypes m_damage;
    public HitType m_hitType;

    public HitData(float damage)
    {
        m_damage.m_damage = damage;
    }
}

public sealed class WearNTear
{
    public float m_health = 100f;
    public float CurrentHealth = 100f;

    public void Damage(HitData hit)
    {
        CurrentHealth -= hit.m_damage.m_damage;
    }
}

public enum ConnectionType
{
    Portal
}

public sealed class MockZdo
{
    public string DestinationId;
    public string GetConnectionZDOID(ConnectionType type) { return DestinationId; }
}

public sealed class MockZNetView
{
    public MockZdo Zdo;
    public MockZdo GetZDO() { return Zdo; }
}

public sealed class ZNetScene
{
    public static ZNetScene instance { get; } = new ZNetScene();
    public object Destination;

    public object FindInstance(string id)
    {
        return id == "destination" ? Destination : null;
    }
}

public sealed class TeleportWorld
{
    public bool m_allowAllItems;
    public MockColor m_colorTargetfound = new MockColor(1f, 1f, 1f, 1f);
    public MockEffectFade m_target_found;
    public WearNTear Wear = new WearNTear();
    public MockAudioSource Audio = new MockAudioSource();
    public MockZNetView m_nview;

    public TeleportWorld()
    {
        m_target_found = new MockEffectFade(m_colorTargetfound);
    }

    public object GetComponent(Type componentType)
    {
        return componentType == typeof(WearNTear) ? Wear : null;
    }

    public object[] GetComponentsInChildren(Type componentType, bool includeInactive)
    {
        return componentType == typeof(MockAudioSource)
            ? new object[] { Audio }
            : new object[0];
    }
}

internal static class MockPortalFlow
{
    private static Type Patches;
    private static MethodInfo PortalPrefix;
    private static MethodInfo PortalPostfix;
    private static MethodInfo TeleportablePostfix;
    private static MethodInfo TeleportToPostfix;
    private static MethodInfo PortalUpdatePrefix;
    private static MethodInfo PortalUpdatePostfix;
    private static MethodInfo UpdatePortalVisuals;
    private static MethodInfo ApplyPortalPulseFrame;
    private static MethodInfo UpdatePendingPortalDamage;

    private static ItemData Item(string prefabName, bool teleportable, int stack, int toolTier = 0)
    {
        return new ItemData
        {
            m_stack = stack,
            m_dropPrefab = new DropPrefab { name = prefabName },
            m_shared = new SharedData
            {
                m_name = "$item_" + prefabName.ToLowerInvariant(),
                m_teleportable = teleportable,
                m_toolTier = toolTier
            }
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static int Count(Player player, string prefabName)
    {
        return player.GetInventory().GetAllItems()
            .Where(item => item.m_dropPrefab.name == prefabName)
            .Sum(item => item.m_stack);
    }

    private static bool PatchedIsTeleportable(Player player, bool allowAllItems)
    {
        object[] arguments = { player, player.IsTeleportable(allowAllItems) };
        TeleportablePostfix.Invoke(null, arguments);
        return (bool)arguments[1];
    }

    private static void Travel(TeleportWorld portal, Player player)
    {
        Player.m_localPlayer = player;
        object[] portalArguments = { player };
        PortalPrefix.Invoke(null, new object[] { portal, portalArguments });

        if (PatchedIsTeleportable(player, portal.m_allowAllItems))
        {
            bool result = player.TeleportTo(0, 0, true);
            object[] teleportArguments = { player, result };
            TeleportToPostfix.Invoke(null, teleportArguments);
        }

        PortalPostfix.Invoke(null, new object[] { portalArguments });
    }

    public static int Main()
    {
        Patches = typeof(UnstablePortalsMetalTransport.UnstablePortalsMetalTransportPlugin).Assembly
            .GetType("UnstablePortalsMetalTransport.PortalPatches", true);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Patches.GetField("_playerType", flags).SetValue(null, typeof(Player));
        Patches.GetField("_wearNTearType", flags).SetValue(null, typeof(WearNTear));
        Patches.GetField("_hitDataType", flags).SetValue(null, typeof(HitData));
        Patches.GetField("_zNetSceneType", flags).SetValue(null, typeof(ZNetScene));
        Patches.GetField("_audioSourceType", flags).SetValue(null, typeof(MockAudioSource));
        Patches.GetField("_log", flags).SetValue(null, new ManualLogSource("UnstablePortalsMetalTransport.Tests"));
        PortalPrefix = Patches.GetMethod("PortalTeleportPrefix", flags);
        PortalPostfix = Patches.GetMethod("PortalTeleportPostfix", flags);
        TeleportablePostfix = Patches.GetMethod("HumanoidIsTeleportablePostfix", flags);
        TeleportToPostfix = Patches.GetMethod("PlayerTeleportToPostfix", flags);
        PortalUpdatePrefix = Patches.GetMethod("PortalUpdatePrefix", flags);
        PortalUpdatePostfix = Patches.GetMethod("PortalUpdatePostfix", flags);
        UpdatePortalVisuals = Patches.GetMethod("UpdatePortalVisuals", flags);
        ApplyPortalPulseFrame = Patches.GetMethod("ApplyPortalPulseFrame", flags);
        UpdatePendingPortalDamage = Patches.GetMethod("UpdatePendingPortalDamage", flags);

        Player ordinary = new Player();
        ordinary.GetInventory().Add(Item("Wood", true, 10));
        ordinary.GetInventory().Add(Item("SurtlingCore", true, 2));
        ordinary.GetInventory().Add(Item("GreydwarfEye", true, 10));
        TeleportWorld ordinaryPortal = new TeleportWorld();
        Travel(ordinaryPortal, ordinary);
        Assert(ordinary.TeleportStarted, "Ordinary travel should succeed.");
        Assert(Count(ordinary, "SurtlingCore") == 2, "Ordinary travel must be free.");
        Assert(Count(ordinary, "GreydwarfEye") == 10, "Ordinary travel must not consume Eyes.");
        Assert(ordinaryPortal.Wear.CurrentHealth == 100f, "Ordinary travel must not damage a portal.");

        Player noCore = new Player();
        noCore.GetInventory().Add(Item("CopperOre", false, 10));
        Travel(new TeleportWorld(), noCore);
        Assert(!noCore.TeleportStarted, "Restricted travel without a Core must fail.");
        Assert(noCore.Messages.Any(message =>
                message.Contains("1 Surtling Core") && message.Contains("5 Greydwarf Eyes")),
            "The configured toll requirement should be shown.");

        Player stackOfCores = new Player();
        stackOfCores.GetInventory().Add(Item("Iron", false, 30));
        stackOfCores.GetInventory().Add(Item("SurtlingCore", true, 2));
        stackOfCores.GetInventory().Add(Item("GreydwarfEye", true, 10));
        Player.m_localPlayer = stackOfCores;
        TeleportWorld purplePortal = new TeleportWorld();
        TeleportWorld destinationPortal = new TeleportWorld();
        ZNetScene.instance.Destination = destinationPortal;
        purplePortal.m_nview = new MockZNetView
        {
            Zdo = new MockZdo { DestinationId = "destination" }
        };
        MockColor originalPortalColor = purplePortal.m_colorTargetfound;
        PortalUpdatePrefix.Invoke(null, new object[] { purplePortal });
        Assert(PatchedIsTeleportable(stackOfCores, false),
            "Metal plus a Core should activate the portal's normal effect.");
        PortalUpdatePostfix.Invoke(null, new object[] { purplePortal });

        object visualStates = Patches.GetField("PortalVisualStates", flags).GetValue(null);
        MethodInfo tryGetVisualState = visualStates.GetType().GetMethod("TryGetValue");
        object[] visualStateArguments = { purplePortal, null };
        Assert((bool)tryGetVisualState.Invoke(visualStates, visualStateArguments),
            "The toll portal should have a captured visual state.");
        object visualState = visualStateArguments[1];

        ApplyPortalPulseFrame.Invoke(null, new[] { (object)purplePortal, visualState, 0f });
        Assert(purplePortal.m_colorTargetfound.B > purplePortal.m_colorTargetfound.R,
            "The frame runes should be purple while the portal effect is dark orange.");
        Assert(purplePortal.m_colorTargetfound.B > 2.5f,
            "The frame runes should retain visible HDR emission brightness.");
        Assert(purplePortal.m_target_found.Particle.StartColor.Color.R >
               purplePortal.m_target_found.Particle.StartColor.Color.B,
            "One end of the portal-effect pulse should be dark orange.");
        Assert(purplePortal.m_target_found.Light.color.R > purplePortal.m_target_found.Light.color.B,
            "The portal light should be dark orange opposite the purple frame runes.");
        Assert(Math.Abs(purplePortal.Audio.pitch - 0.82f) < 0.001f,
            "The instability sound should reach its deeper pitch endpoint.");

        ApplyPortalPulseFrame.Invoke(null, new[] { (object)purplePortal, visualState, 1f });
        Assert(purplePortal.m_colorTargetfound.R > purplePortal.m_colorTargetfound.B,
            "The frame runes should be dark orange while the portal effect is purple.");
        Assert(purplePortal.m_colorTargetfound.R >= 3f,
            "The orange frame-rune endpoint should retain HDR emission brightness.");
        Assert(purplePortal.m_target_found.Particle.StartColor.Color.B >
               purplePortal.m_target_found.Particle.StartColor.Color.G,
            "The toll portal's existing particle animation should pulse purple.");
        Assert(purplePortal.m_target_found.Light.color.B > purplePortal.m_target_found.Light.color.G,
            "The toll portal's existing light should pulse purple.");
        Assert(Math.Abs(purplePortal.Audio.pitch - 0.96f) < 0.001f,
            "The instability sound should wobble toward its upper pitch endpoint.");
        UpdatePortalVisuals.Invoke(null, null);
        Assert(Count(stackOfCores, "SurtlingCore") == 2,
            "Merely activating the portal effect must not charge a Core.");
        Assert(Count(stackOfCores, "GreydwarfEye") == 10,
            "Merely activating the portal effect must not charge Eyes.");
        Travel(purplePortal, stackOfCores);
        Assert(stackOfCores.TeleportStarted, "Restricted travel with a Core should succeed.");
        Assert(Count(stackOfCores, "SurtlingCore") == 1, "Exactly one Core should be consumed.");
        Assert(Count(stackOfCores, "GreydwarfEye") == 5, "Exactly five Eyes should be consumed.");
        Assert(purplePortal.Wear.CurrentHealth == 90f,
            "A paid trip should damage the source portal by 10% of maximum health.");
        UpdatePendingPortalDamage.Invoke(null, null);
        Assert(destinationPortal.Wear.CurrentHealth == 90f,
            "A paid trip should damage the linked destination portal after it loads.");

        Player normalVisualPlayer = new Player();
        normalVisualPlayer.GetInventory().Add(Item("Wood", true, 1));
        Player.m_localPlayer = normalVisualPlayer;
        PortalUpdatePrefix.Invoke(null, new object[] { purplePortal });
        Assert(PatchedIsTeleportable(normalVisualPlayer, false), "Normal cargo should remain teleportable.");
        PortalUpdatePostfix.Invoke(null, new object[] { purplePortal });
        Assert(purplePortal.m_colorTargetfound.R == originalPortalColor.R &&
               purplePortal.m_colorTargetfound.G == originalPortalColor.G &&
               purplePortal.m_colorTargetfound.B == originalPortalColor.B,
            "The portal's original color should be restored outside the toll case.");
        Assert(Math.Abs(purplePortal.Audio.pitch - 1f) < 0.001f,
            "The portal's original audio pitch should be restored outside the toll case.");

        Player lastCore = new Player();
        lastCore.GetInventory().Add(Item("SilverOre", false, 1));
        lastCore.GetInventory().Add(Item("SurtlingCore", true, 1));
        lastCore.GetInventory().Add(Item("GreydwarfEye", true, 5));
        Travel(new TeleportWorld(), lastCore);
        Assert(lastCore.TeleportStarted, "Travel with the final Core should succeed.");
        Assert(Count(lastCore, "SurtlingCore") == 0, "A depleted Core stack should be removed.");
        Assert(Count(lastCore, "GreydwarfEye") == 0, "A depleted Eye stack should be removed.");

        Player failedTeleport = new Player { RejectTeleport = true };
        failedTeleport.GetInventory().Add(Item("TinOre", false, 1));
        failedTeleport.GetInventory().Add(Item("SurtlingCore", true, 2));
        failedTeleport.GetInventory().Add(Item("GreydwarfEye", true, 10));
        Travel(new TeleportWorld(), failedTeleport);
        Assert(Count(failedTeleport, "SurtlingCore") == 2,
            "A teleport that does not start must not be charged.");
        Assert(Count(failedTeleport, "GreydwarfEye") == 10,
            "A failed teleport must not consume Eyes.");

        Player absoluteBlock = new Player();
        absoluteBlock.GetInventory().Add(Item("QuestItem", false, 1, 1000));
        absoluteBlock.GetInventory().Add(Item("SurtlingCore", true, 1));
        absoluteBlock.GetInventory().Add(Item("GreydwarfEye", true, 5));
        Travel(new TeleportWorld(), absoluteBlock);
        Assert(!absoluteBlock.TeleportStarted, "Absolute restrictions must remain blocked.");
        Assert(Count(absoluteBlock, "SurtlingCore") == 1, "A blocked trip must not be charged.");

        Player allItemsPortal = new Player();
        allItemsPortal.GetInventory().Add(Item("BlackMetal", false, 1));
        allItemsPortal.GetInventory().Add(Item("SurtlingCore", true, 1));
        allItemsPortal.GetInventory().Add(Item("GreydwarfEye", true, 5));
        Travel(new TeleportWorld { m_allowAllItems = true }, allItemsPortal);
        Assert(allItemsPortal.TeleportStarted, "An allow-all-items portal should still work.");
        Assert(Count(allItemsPortal, "SurtlingCore") == 1, "An allow-all-items portal must be free.");
        Assert(Count(allItemsPortal, "GreydwarfEye") == 5,
            "An allow-all-items portal must not consume Eyes.");

        Console.WriteLine("All mock portal-flow tests passed.");
        return 0;
    }
}
