using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rebuilt;

[RequireComponent(typeof(Piece))]
[RequireComponent(typeof(WearNTear))]
public class GhostPiece : MonoBehaviour
{
    private static readonly Material GhostMat = RebuiltPlugin.GetAssetBundle("rebuiltbundle").LoadAsset<Material>("GhostPiece_mat");
    private static readonly int GhostedHash = "GhostPiece".GetStableHashCode();
    private static readonly List<GhostPiece> m_instances = new();
    public ZNetView m_nview = null!;
    public Piece m_piece = null!;
    public WearNTear m_wearNTear = null!;
    private readonly Dictionary<Collider, bool> m_colliderTriggers = new();
    private readonly Dictionary<Renderer, Material[]> m_rendererMaterials = new();

    public bool m_supports;
    public int m_comfort;
    public void Awake()
    {
        m_nview = GetComponent<ZNetView>();
        m_piece = GetComponent<Piece>();
        m_wearNTear = GetComponent<WearNTear>();
        m_supports = m_wearNTear.m_supports;
        m_comfort = m_piece.m_comfort;
        foreach (var collider in GetComponentsInChildren<Collider>())
        {
            m_colliderTriggers[collider] = collider.isTrigger;
        }
        m_instances.Add(this);
    }

    public void OnDestroy()
    {
        m_instances.Remove(this);
    }

    public void OnEnable()
    {
        if (!m_nview || !m_nview.IsValid() || m_nview.GetZDO() == null) return;
        if (!IsGhost()) return;
        if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.On)
        {
            Ghost(null, true);
        }
        else
        {
            Remove(RebuiltPlugin._requireResources.Value is RebuiltPlugin.Toggle.Off);
        }
    }

    public void RemoveBedSpawnPoint()
    {
        if (!TryGetComponent(out Bed bed) || !m_nview.IsOwner() || Game.instance == null) return;
        Game.instance.RemoveCustomSpawnPoint(bed.GetSpawnPoint());
    }

    public void Destroy(HitData? hitData, bool blockDrop, bool ghost = true)
    {
        RemoveBedSpawnPoint();
        m_nview.GetZDO().Set(ZDOVars.s_health, 0.0f);
        if (ghost)
        {
            if (RebuiltPlugin._ghostSupports.Value is RebuiltPlugin.Toggle.Off)
            {
                m_wearNTear.m_supports = false;
            }

            m_piece.m_comfort = 0;
        }
        else
        {
            m_nview.GetZDO().Set(ZDOVars.s_support, 0.0f);
            m_wearNTear.m_support = 0.0f;
            m_wearNTear.m_health = 0.0f;
            m_wearNTear.ClearCachedSupport();
        }
        DropResources(blockDrop);

        m_wearNTear.m_onDestroyed?.Invoke();

        if (m_wearNTear.m_destroyNoise > 0.0 && hitData != null && hitData.m_hitType != HitData.HitType.CinderFire)
        {
            Player closestPlayer = Player.GetClosestPlayer(m_wearNTear.transform.position, 10f);
            if (closestPlayer) closestPlayer.AddNoise(m_wearNTear.m_destroyNoise);
        }

        var transform1 = transform;
        m_wearNTear.m_destroyedEffect.Create(transform1.position, transform1.rotation, transform1);
        if (m_wearNTear.m_autoCreateFragments)
        {
            m_nview.InvokeRPC(ZNetView.Everybody, "RPC_CreateFragments");
        }

        if (!ghost) ZNetScene.instance.Destroy(gameObject);
    }

    public void DropResources(bool blockDrop)
    {
        if (!m_piece || blockDrop || RebuiltPlugin._requireResources.Value is RebuiltPlugin.Toggle.Off) return;
        m_piece.DropResources();
    }

    public bool InActivePlayerWard()
    {
        if (RebuiltPlugin._requireWard.Value is RebuiltPlugin.Toggle.Off) return true;
        foreach (var area in PrivateArea.m_allAreas)
        {
            if (area.IsEnabled() && area.m_ownerFaction == Character.Faction.Players &&
                area.IsInside(transform.position, 0.0f))
            {
                return true;
            }
        }

        return false;
    }

    public void Ghost(HitData? hitData, bool blockDrop)
    {
        if (m_nview == null) return;
        if (!InActivePlayerWard())
        {
            Remove(blockDrop);
        }
        else
        {
            Destroy(hitData, blockDrop, true);
            EnableComponents(false);
            SetupGhostMaterials<MeshRenderer>();
            SetupGhostMaterials<SkinnedMeshRenderer>();
            SetGhosted(true);
        }
    }
    
    public bool Rebuild()
    {
        if (RebuiltPlugin._requireResources.Value is RebuiltPlugin.Toggle.On)
        {
            if (!Player.m_localPlayer) return false;
            if (!Player.m_localPlayer.NoCostCheat())
            {
                if (!Player.m_localPlayer.HaveRequirements(m_piece, Player.RequirementMode.CanBuild)) return false;
                Player.m_localPlayer.ConsumeResources(m_piece.m_resources, 0);
            }
        }
        m_wearNTear.m_supports = m_supports;
        m_piece.m_comfort = m_comfort;
        EnableComponents(true);
        ResetMaterials();
        m_wearNTear.UpdateSupport();
        SetGhosted(false);
        return true;
    }

    public void SetGhosted(bool isGhost) => m_nview.GetZDO().Set(GhostedHash, isGhost);
    
    public void EnableColliders(bool enable)
    {
        foreach (KeyValuePair<Collider, bool> kvp in m_colliderTriggers)
        {
            if (kvp.Key is MeshCollider { convex: false })
            {
                kvp.Key.enabled = enable;
            }
            else
            {
                kvp.Key.isTrigger = !enable || kvp.Value;
            }

        }
    }

    public void EnableComponents(bool enable)
    {
        EnableColliders(enable);
        EnableParticles(enable);
        EnableComponent<ParticleSystemForceField>(enable);
        EnableComponent<Demister>(enable);
        EnableComponent<TerrainModifier>(enable);
        EnableComponent<GuidePoint>(enable);
        EnableComponent<LightLod>(enable);
        EnableComponent<LightFlicker>(enable);
        EnableComponent<Light>(enable);
        EnableComponent<AudioSource>(enable);
        EnableComponent<ZSFX>(enable);
        EnableComponent<WispSpawner>(enable);
        EnableComponent<Windmill>(enable);
        EnableComponent<Aoe>(enable);
        EnableComponent<SmokeSpawner>(enable);
        EnableComponent<EffectArea>(enable);
        EnableComponent<EffectFade>(enable);
        EnableFirePlace(enable);
    }

    public void EnableParticles(bool enable)
    {
        foreach (var component in GetComponentsInChildren<ParticleSystem>(true))
        {
            component.gameObject.SetActive(enable);
        }

    }
    
    public void EnableFirePlace(bool enable)
    {
        if (!TryGetComponent(out Fireplace component)) return;
        if (!enable) component.SetFuel(0f);
    }

    public void EnableComponent<T>(bool enable) where T : Behaviour
    {
        foreach (var component in GetComponentsInChildren<T>(true))
        {
            component.enabled = enable;
        }
    }

    public void Remove(bool blockDrop)
    {
        if (!m_nview.IsValid() || !m_nview.IsOwner()) return;
        Destroy(null, blockDrop || IsGhost(), false);
    }

    public bool IsGhost() => m_nview.IsValid() && m_nview.GetZDO().GetBool(GhostedHash);

    public void SetupGhostMaterials<T>() where T : Renderer
    {
        foreach (T renderer in GetComponentsInChildren<T>(true))
        {
            if (renderer.sharedMaterials == null) continue;
            Material[] sharedMaterials = renderer.sharedMaterials;
            m_rendererMaterials[renderer] = sharedMaterials.ToArray();
            for (int index = 0; index < sharedMaterials.Length; ++index)
            {
                var mat = sharedMaterials[index];
                var matName = mat.name.Replace("(Instance)", string.Empty).Trim();
                Material material = new Material(GhostMat);
                if (material.HasProperty("_MainTex") && material.mainTexture != null) material.mainTexture = mat.mainTexture;
                if (material.HasProperty("_Color")) material.color = new Color(mat.color.r, mat.color.g, mat.color.b, RebuiltPlugin._transparency.Value);
                material.name = matName;
                sharedMaterials[index] = material;
            }

            renderer.sharedMaterials = sharedMaterials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    public void ResetMaterials()
    {
        foreach (var kvp in m_rendererMaterials)
        {
            if (kvp.Key == null) continue;
            kvp.Key.sharedMaterials = kvp.Value;
            kvp.Key.shadowCastingMode = ShadowCastingMode.On;
        }
    }
    
    public static void OnEnableConfigChange(object sender, EventArgs args)
    {
        if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.On) return;
        foreach (var instance in m_instances)
        {
            if (!instance.IsGhost()) continue;
            instance.Remove(RebuiltPlugin._requireResources.Value is RebuiltPlugin.Toggle.Off);
        }
    }
    
    
    public static void OnSupportConfigChange(object sender, EventArgs args)
    {
        foreach (var instance in m_instances)
        {
            if (!instance.IsGhost()) continue;
            instance.m_wearNTear.m_supports =
                instance.m_supports && RebuiltPlugin._ghostSupports.Value is RebuiltPlugin.Toggle.On;
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_Remove))]
    private static class WearNTear_RPC_Remove_Patch
    {
        private static bool Prefix(WearNTear __instance, bool blockDrop)
        {
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return true;
            if (!__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return true;
            if (!__instance.TryGetComponent(out GhostPiece component)) return true;
            component.Remove(blockDrop);
            return false;
        }
    }
    
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
    private static class WearNTear_Destroy_Patch
    {
        private static bool Prefix(WearNTear __instance, HitData? hitData = null, bool blockDrop = false)
        {
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return true;
            if (!__instance.TryGetComponent(out GhostPiece component)) return true;
            if (__instance.m_piece.m_creator == 0L && RebuiltPlugin._hasCreator.Value is RebuiltPlugin.Toggle.On) return true;
            component.Ghost(hitData, blockDrop);
            return false;
        }
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.UpdateBuild))]
    private static class Hud_UpdateBuild_Patch
    {
        private static void Postfix(Hud __instance)
        {
            if (!Player.m_localPlayer) return;
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return;
            if (RebuiltPlugin._requireResources.Value is RebuiltPlugin.Toggle.Off) return;
            if (Player.m_localPlayer.GetHoveringPiece() is { } piece && piece.TryGetComponent(out GhostPiece component) && component.IsGhost())
            {
                __instance.SetupPieceInfo(piece);
            }
        }
    }
    
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Repair))]
    private static class WearNTear_Repair_Patch
    {
        private static bool Prefix(WearNTear __instance)
        {
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return true;
            if (!__instance.TryGetComponent(out GhostPiece component)) return true;
            if (!component.IsGhost()) return true;
            return component.Rebuild();
        }
    }
    
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    private static class ZNetScene_Awake_Patch
    {
        private static void Postfix(ZNetScene __instance)
        {
            var blacklist = new RebuiltPlugin.SerializedNameList(RebuiltPlugin._blacklist.Value).m_names;
            foreach (var prefab in __instance.m_prefabs)
            {
                if (!prefab.GetComponent<Piece>() || !prefab.GetComponent<WearNTear>()) continue;
                if (blacklist.Contains(prefab.name) || prefab.GetComponent<Ship>()) continue;
                prefab.AddComponent<GhostPiece>();
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Interact))]
    private static class Player_Interact_Patch
    {
        private static bool Prefix(GameObject go)
        {
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return true;
            GhostPiece component = go.GetComponentInParent<GhostPiece>();
            if (component == null) return true;
            return !component.IsGhost();
        }
    }

    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
    private static class Humanoid_UseItem_Patch
    {
        private static bool Prefix(Humanoid __instance)
        {
            return __instance.GetHoverObject() is not { } go || go.GetComponentInParent<GhostPiece>() is not { } componentInParent || !componentInParent.IsGhost();
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport))]
    private static class TeleportWorld_Teleport_Patch
    {
        private static bool Prefix(TeleportWorld __instance)
        {
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return true;
            if (!__instance.TryGetComponent(out GhostPiece component)) return true;
            return !component.IsGhost();
        }
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.UpdateCrosshair))]
    private static class Hud_UpdateCrossHair_Patch
    {
        private static void Postfix(Hud __instance, Player player)
        {
            if (RebuiltPlugin._enabled.Value is RebuiltPlugin.Toggle.Off) return;
            if (player.GetHoverObject() is not { } obj) return;
            var component = obj.GetComponentInParent<GhostPiece>();
            if (component != null && component.IsGhost())
            {
                __instance.m_hoverName.text = Localization.instance.Localize("$hover_use_repair_hammer");
            }
        }
    }

    [HarmonyPatch(typeof(WispSpawner), nameof(WispSpawner.GetStatus))]
    private static class WispSpawner_GetStatus_Patch
    {
        private static void Postfix(WispSpawner __instance, ref WispSpawner.Status __result)
        {
            if (!__instance.TryGetComponent(out GhostPiece component) || !component.IsGhost()) return;
            __result = WispSpawner.Status.NoSpace;
        }
    }
}