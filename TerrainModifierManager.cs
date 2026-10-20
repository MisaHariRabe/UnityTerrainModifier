using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// À placer sur le Terrain. Conserve une copie "base" de la heightmap et recalcule
/// base + modificateurs (triés par layerOrder) de façon non destructive.
/// Plusieurs terrains (plusieurs managers) sont supportés : chaque modificateur
/// n'affecte que les terrains qu'il recouvre.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Terrain))]
[AddComponentMenu("Terrain/Terrain Modifier Manager")]
public class TerrainModifierManager : MonoBehaviour
{
    // ---------- Registre statique ----------
    private static readonly List<ITerrainModifier> modifiers = new List<ITerrainModifier>();
    private static readonly List<TerrainModifierManager> managers = new List<TerrainModifierManager>();

    public static IReadOnlyList<ITerrainModifier> Modifiers => modifiers;
    public static IReadOnlyList<TerrainModifierManager> Managers => managers;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // Unity 6 : "Enter Play Mode" sans rechargement de domaine => on nettoie les statiques.
        modifiers.Clear();
        managers.Clear();
    }

    public static void Register(ITerrainModifier m)
    {
        if (m == null) return;
        if (!modifiers.Contains(m)) modifiers.Add(m);
        RequestUpdate();
    }

    public static void Unregister(ITerrainModifier m)
    {
        modifiers.Remove(m);
        RequestUpdate();
    }

    public static void RequestUpdate()
    {
        for (int i = 0; i < managers.Count; i++) managers[i].MarkDirty();
    }

    /// <summary>Hauteur monde de la heightmap de BASE (sans modificateurs). Évite les boucles de rétroaction.</summary>
    public static bool TrySampleBaseHeight(float worldX, float worldZ, out float worldY)
    {
        for (int i = 0; i < managers.Count; i++)
            if (managers[i].TrySampleBaseInternal(worldX, worldZ, out worldY)) return true;

        // Terrain sans manager (ou base indisponible) : on lit la hauteur actuelle du terrain.
        Vector3 p = new Vector3(worldX, 0f, worldZ);
        Terrain t = FindTerrainAt(p);
        if (t != null)
        {
            worldY = t.SampleHeight(p) + t.transform.position.y;
            return true;
        }

        worldY = 0f;
        return false;
    }

    /// <summary>Terrain actif dont l'emprise XZ contient ce point (null si aucun).</summary>
    public static Terrain FindTerrainAt(Vector3 world)
    {
        foreach (var t in Terrain.activeTerrains)
        {
            var d = t.terrainData;
            if (d == null) continue;
            Vector3 o = t.transform.position;
            if (world.x >= o.x && world.z >= o.z && world.x <= o.x + d.size.x && world.z <= o.z + d.size.z)
                return t;
        }
        return null;
    }

    /// <summary>Ajoute un manager à chaque terrain recouvert par la boîte (XZ) qui n'en possède pas encore.</summary>
    public static int EnsureManagersForBounds(Bounds wb)
    {
        int added = 0;
        foreach (var t in Terrain.activeTerrains)
        {
            var d = t.terrainData;
            if (d == null || t.GetComponent<TerrainModifierManager>() != null) continue;

            Vector3 o = t.transform.position;
            bool overlap = o.x < wb.max.x && o.x + d.size.x > wb.min.x &&
                           o.z < wb.max.z && o.z + d.size.z > wb.min.z;
            if (!overlap) continue;

#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.AddComponent<TerrainModifierManager>(t.gameObject);
            else t.gameObject.AddComponent<TerrainModifierManager>();
#else
            t.gameObject.AddComponent<TerrainModifierManager>();
#endif
            Debug.Log($"[TerrainModifierManager] Manager ajouté automatiquement au terrain '{t.name}'.", t);
            added++;
        }
        return added;
    }

    // ---------- Paramètres ----------
    [Tooltip("Recalcule automatiquement le terrain quand un modificateur change.")]
    public bool autoUpdate = true;

    [Tooltip("Restaure la heightmap de base quand le manager est désactivé.")]
    public bool restoreBaseOnDisable = false;

    [SerializeField, HideInInspector] private float[] baseHeights;
    [SerializeField, HideInInspector] private int baseResolution;
    [SerializeField, HideInInspector] private bool sculptMode;

    // ---------- État ----------
    private Terrain terrain;
    private TerrainData terrainData;
    private float[,] work;
    private bool dirty;
    private bool scheduled;

    public double LastApplyMs { get; private set; }
    public int LastModifierCount { get; private set; }
    public bool HasBase => baseHeights != null && baseResolution > 0;
    public bool SculptMode => sculptMode;
    public float BaseMemoryMB => HasBase ? baseHeights.Length * sizeof(float) / (1024f * 1024f) : 0f;

    private float rangeMin, rangeMax;
    private bool rangeValid;

    /// <summary>Hauteurs monde min/max de la base capturée (diagnostic : une base plate à 0 m est suspecte).</summary>
    public bool TryGetBaseRange(out float minMeters, out float maxMeters)
    {
        minMeters = maxMeters = 0f;
        if (!HasBase || terrainData == null) return false;
        if (!rangeValid)
        {
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < baseHeights.Length; i++)
            {
                float v = baseHeights[i];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            rangeMin = mn; rangeMax = mx; rangeValid = true;
        }
        minMeters = transform.position.y + rangeMin * terrainData.size.y;
        maxMeters = transform.position.y + rangeMax * terrainData.size.y;
        return true;
    }

    // ---------- Cycle de vie ----------
    private void OnEnable()
    {
        if (!managers.Contains(this)) managers.Add(this);
#if UNITY_EDITOR
        Undo.undoRedoPerformed += OnUndoRedo;
#endif
        if (EnsureReady()) MarkDirty();
    }

    private void OnDisable()
    {
        managers.Remove(this);
#if UNITY_EDITOR
        Undo.undoRedoPerformed -= OnUndoRedo;
#endif
        if (restoreBaseOnDisable && HasBase && terrain != null && terrain.terrainData != null)
            RestoreBase();
    }

    private void LateUpdate()
    {
        if (!dirty) return;
        dirty = false;
        ApplyModifications();
    }

#if UNITY_EDITOR
    private void OnUndoRedo() => MarkDirty();
    private void OnValidate() => MarkDirty();
#endif

    public void MarkDirty()
    {
        if (!autoUpdate || sculptMode) return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            if (scheduled) return;
            scheduled = true;
            EditorApplication.delayCall += DelayedApply;
            return;
        }
#endif
        dirty = true;
    }

#if UNITY_EDITOR
    private void DelayedApply()
    {
        scheduled = false;
        if (this == null || !isActiveAndEnabled) return;
        ApplyModifications();
    }
#endif

    // ---------- Base ----------
    private bool EnsureReady()
    {
        if (terrain == null) terrain = GetComponent<Terrain>();
        if (terrain == null || terrain.terrainData == null) return false;
        terrainData = terrain.terrainData;

        int res = terrainData.heightmapResolution;
        if (!HasBase || baseResolution != res)
        {
            if (HasBase)
                Debug.LogWarning($"[TerrainModifierManager] La résolution de la heightmap a changé ({baseResolution} → {res}) : la base est recapturée.", this);
            CaptureBase();
        }
        return true;
    }

    /// <summary>Capture la heightmap actuelle du terrain comme nouvelle base.</summary>
    public void CaptureBase()
    {
        if (terrain == null) terrain = GetComponent<Terrain>();
        if (terrain == null || terrain.terrainData == null) return;
        terrainData = terrain.terrainData;

        // Si le terrain a été modifié côté GPU (outils de génération), on resynchronise la copie CPU avant lecture.
        terrainData.SyncHeightmap();
        rangeValid = false;

        int res = terrainData.heightmapResolution;
        float[,] h = terrainData.GetHeights(0, 0, res, res);
        if (baseHeights == null || baseHeights.Length != res * res) baseHeights = new float[res * res];
        Buffer.BlockCopy(h, 0, baseHeights, 0, baseHeights.Length * sizeof(float));
        baseResolution = res;
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Réinitialiser le Terrain de Base")]
    public void ResetToBase() => RestoreBase();

    /// <summary>Réécrit la heightmap de base dans le terrain (sans modificateurs).</summary>
    public void RestoreBase()
    {
        if (!HasBase || terrainData == null) return;
        int res = baseResolution;
        if (work == null || work.GetLength(0) != res) work = new float[res, res];
        Buffer.BlockCopy(baseHeights, 0, work, 0, baseHeights.Length * sizeof(float));
        terrainData.SetHeights(0, 0, work);
        MarkTerrainDirty();
    }

    /// <summary>Mode sculpt : on retire les modificateurs pour pouvoir peindre le terrain avec les outils Unity.</summary>
    public void BeginSculptMode()
    {
        if (sculptMode || !EnsureReady()) return;
        RestoreBase();
        sculptMode = true;
    }

    /// <summary>Fin du mode sculpt : le résultat devient la nouvelle base et les modificateurs sont ré-appliqués.</summary>
    public void EndSculptMode()
    {
        if (!sculptMode) return;
        CaptureBase();
        sculptMode = false;
        ApplyModifications();
    }

    /// <summary>Applique les modificateurs puis fige le résultat comme nouvelle base.</summary>
    public void BakeCurrent()
    {
        ApplyModifications();
        CaptureBase();
    }

    // ---------- Calcul ----------
    public void ApplyModifications()
    {
        if (sculptMode || !EnsureReady()) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int res = baseResolution;
        if (work == null || work.GetLength(0) != res) work = new float[res, res];
        Buffer.BlockCopy(baseHeights, 0, work, 0, baseHeights.Length * sizeof(float));

        var ctx = new TerrainHeightContext(work, res, transform.position, terrainData.size);

        int count = 0;
        foreach (var m in modifiers.Where(IsActive).OrderBy(m => m.LayerOrder))
        {
            try { m.Apply(ctx); count++; }
            catch (Exception e) { Debug.LogException(e, m.Owner); }
        }

        terrainData.SetHeights(0, 0, work);
        MarkTerrainDirty();

        sw.Stop();
        LastApplyMs = sw.Elapsed.TotalMilliseconds;
        LastModifierCount = count;
    }

    private static bool IsActive(ITerrainModifier m) => m != null && m.Owner != null && m.Owner.isActiveAndEnabled;

    private void MarkTerrainDirty()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && terrainData != null) EditorUtility.SetDirty(terrainData);
#endif
    }

    private bool TrySampleBaseInternal(float wx, float wz, out float worldY)
    {
        worldY = 0f;
        if (!HasBase || terrainData == null) return false;

        int res = baseResolution;
        Vector3 o = transform.position;
        Vector3 s = terrainData.size;
        float fx = (wx - o.x) / s.x * (res - 1);
        float fz = (wz - o.z) / s.z * (res - 1);
        if (fx < 0f || fz < 0f || fx > res - 1 || fz > res - 1) return false;

        int x = Mathf.Min((int)fx, res - 2), z = Mathf.Min((int)fz, res - 2);
        float tx = fx - x, tz = fz - z;
        float a = Mathf.Lerp(baseHeights[z * res + x], baseHeights[z * res + x + 1], tx);
        float b = Mathf.Lerp(baseHeights[(z + 1) * res + x], baseHeights[(z + 1) * res + x + 1], tx);
        worldY = o.y + Mathf.Lerp(a, b, tz) * s.y;
        return true;
    }
}