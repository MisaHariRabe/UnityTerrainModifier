using UnityEngine;

/// <summary>Quelle hauteur le terrain doit-il viser sous le bâtiment ?</summary>
public enum FootprintGroundMode
{
    Average,   // moyenne du terrain d'origine sous l'empreinte (déblai ≈ remblai)
    Highest,   // point le plus haut (on ne creuse jamais plus que nécessaire, on comble le reste)
    Lowest,    // point le plus bas (on creuse partout, on ne comble jamais)
    Center     // hauteur du terrain d'origine au centre de l'empreinte
}

/// <summary>
/// Empreinte de bâtiment : un rectangle orienté (suit la rotation Y de l'objet) sous lequel
/// le terrain est aplani, avec une marge plate puis une zone de raccord en pente douce.
/// Non destructif : c'est un ITerrainModifier comme les autres, la heightmap d'origine n'est jamais modifiée.
/// Déplace / tourne l'objet et le terrain suit instantanément ; supprime le composant et le terrain redevient normal.
/// </summary>
[ExecuteAlways]
[AddComponentMenu("Terrain/Terrain Footprint (bâtiment)")]
public class TerrainFootprint : MonoBehaviour, ITerrainModifier
{
    [Header("Empreinte (en mètres, dans le repère de l'objet tourné en Y)")]
    [Tooltip("Largeur (X) et profondeur (Z) du rectangle aplani.")]
    public Vector2 size = new Vector2(10f, 10f);

    [Tooltip("Décalage du centre du rectangle par rapport au pivot de l'objet (X = droite, Y = avant).")]
    public Vector2 centerOffset = Vector2.zero;

    [Tooltip("Distance du sol plat au-delà du rectangle (trottoir, jardin, cour...).")]
    [Min(0f)] public float margin = 1.5f;

    [Tooltip("Largeur de la zone de raccord (pente douce) entre le sol plat et le terrain naturel.")]
    [Min(0f)] public float blendDistance = 6f;

    [Header("Hauteur")]
    [Tooltip("Hauteur du sol du bâtiment par rapport au bas de l'objet. Positif = le terrain monte au-dessus du bas du mesh, négatif = le bâtiment est enterré (fondations).")]
    public float heightOffset = 0f;

    [Tooltip("Distance verticale entre le pivot de l'objet et le bas de son mesh (mesuré par 'Ajuster à la géométrie').")]
    public float floorOffset = 0f;

    [Header("Terrain")]
    [Tooltip("Blend : aplanit (monte et descend). Raise : comble seulement. Lower : creuse seulement.")]
    public ModifierOperation operation = ModifierOperation.Blend;

    [Range(0f, 1f)] public float strength = 1f;

    [Range(0, 5), Tooltip("Lissage du résultat. 0 = sol parfaitement plat sous le bâtiment.")]
    public int smoothness = 0;

    [Tooltip("Profil de la zone de raccord : 1 au bord du sol plat, 0 au bord extérieur.")]
    public AnimationCurve falloffCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

    public int layerOrder = 10;

    [Header("Affichage")]
    public bool showGizmos = true;

    // ---- ITerrainModifier ----
    public Behaviour Owner => this;
    public int LayerOrder => layerOrder;
    public string Label => gameObject.name;
    public ModifierOperation Operation => operation;

    /// <summary>Hauteur monde visée pour le sol du bâtiment.</summary>
    public float TargetY => transform.position.y + floorOffset + heightOffset;

    public float Reach => margin + blendDistance;

    private void OnEnable()
    {
        transform.hasChanged = false;
        TerrainModifierManager.Register(this);
        TerrainModifierManager.RequestUpdate();
    }

    private void OnDisable()
    {
        TerrainModifierManager.Unregister(this);
        TerrainModifierManager.RequestUpdate();
    }

    private void Update()
    {
        if (transform.hasChanged)
        {
            transform.hasChanged = false;
            TerrainModifierManager.EnsureManagersForBounds(WorldBounds());
            TerrainModifierManager.RequestUpdate();
        }
    }

    private void OnValidate()
    {
        size.x = Mathf.Max(0.5f, size.x);
        size.y = Mathf.Max(0.5f, size.y);
        // Seules ces trois opérations ont un sens pour une empreinte.
        if (operation != ModifierOperation.Blend && operation != ModifierOperation.Raise && operation != ModifierOperation.Lower)
            operation = ModifierOperation.Blend;
        TerrainModifierManager.RequestUpdate();
    }

    // ------------------------------------------------------------------
    //  Repère du bâtiment (rotation Y uniquement)
    // ------------------------------------------------------------------
    private void GetFrame(out Vector2 centerXZ, out Vector2 right, out Vector2 forward)
    {
        float yaw = transform.eulerAngles.y * Mathf.Deg2Rad;
        float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
        right = new Vector2(c, -s);     // axe X local projeté sur XZ
        forward = new Vector2(s, c);    // axe Z local projeté sur XZ
        Vector3 p = transform.position;
        centerXZ = new Vector2(p.x, p.z) + right * centerOffset.x + forward * centerOffset.y;
    }

    /// <summary>Boîte monde (XZ) englobant l'empreinte, marge et raccord compris.</summary>
    public Bounds WorldBounds()
    {
        GetFrame(out var ctr, out _, out _);
        float r = size.magnitude * 0.5f + Reach;
        return new Bounds(new Vector3(ctr.x, transform.position.y, ctr.y), new Vector3(r * 2f, 100000f, r * 2f));
    }

    /// <summary>Convertit un point monde XZ en coordonnées du repère de l'empreinte (centre = 0).</summary>
    public Vector2 ToFrame(float worldX, float worldZ)
    {
        GetFrame(out var ctr, out var right, out var fwd);
        Vector2 d = new Vector2(worldX, worldZ) - ctr;
        return new Vector2(Vector2.Dot(d, right), Vector2.Dot(d, fwd));
    }

    /// <summary>Point monde XZ (y = 0) depuis le repère de l'empreinte.</summary>
    public Vector3 FromFrame(Vector2 f)
    {
        GetFrame(out var ctr, out var right, out var fwd);
        Vector2 w = ctr + right * f.x + fwd * f.y;
        return new Vector3(w.x, 0f, w.y);
    }

    // ------------------------------------------------------------------
    //  Application sur la heightmap
    // ------------------------------------------------------------------
    public void Apply(TerrainHeightContext c)
    {
        GetFrame(out var ctr, out var right, out var fwd);
        Vector2 half = size * 0.5f;
        float reach = Reach;
        float bound = half.magnitude + reach;

        if (!c.TryGetRegion(ctr - Vector2.one * bound, ctr + Vector2.one * bound,
                            out int x0, out int z0, out int w, out int h))
            return;

        float targetNorm = Mathf.Clamp01((TargetY - c.origin.y) / c.size.y);
        float[,] delta = new float[h, w];

        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2 world = c.PixelToWorld(x0 + x, z0 + z);
                Vector2 d = world - ctr;
                float lx = Mathf.Abs(Vector2.Dot(d, right));
                float lz = Mathf.Abs(Vector2.Dot(d, fwd));

                // Distance au rectangle (0 à l'intérieur) => coins arrondis naturellement.
                float dx = Mathf.Max(lx - half.x, 0f);
                float dz = Mathf.Max(lz - half.y, 0f);
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > reach) continue;

                float f = 1f;
                if (dist > margin)
                {
                    float t = blendDistance > 0.001f ? Mathf.Clamp01((dist - margin) / blendDistance) : 1f;
                    f = Mathf.Clamp01(falloffCurve.Evaluate(t));
                }

                float amount = strength * f;
                if (amount <= 0f) continue;

                float current = c.heights[z0 + z, x0 + x];
                delta[z, x] = ModifierMath.Delta(operation, current, targetNorm, 0f, 1f, amount);
            }
        }

        c.ApplyDelta(delta, x0, z0, smoothness);
    }

    // ------------------------------------------------------------------
    //  Outils (appelés par l'éditeur)
    // ------------------------------------------------------------------

    /// <summary>
    /// Mesure la géométrie d'une hiérarchie (MeshFilters) dans le repère "pivot + rotation Y" de la racine.
    /// Marche aussi sur un prefab non instancié. Retourne false s'il n'y a aucun mesh.
    /// </summary>
    public static bool MeasureGeometry(Transform root, out Vector2 center, out Vector2 sizeXZ, out float floorOffsetY)
    {
        center = Vector2.zero; sizeXZ = Vector2.one * 4f; floorOffsetY = 0f;
        var filters = root.GetComponentsInChildren<MeshFilter>(false);
        Vector3 s = root.lossyScale;
        Matrix4x4 worldToRoot = root.worldToLocalMatrix;

        bool any = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        foreach (var mf in filters)
        {
            if (mf.sharedMesh == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled) continue;

            Matrix4x4 m = worldToRoot * mf.transform.localToWorldMatrix;
            Bounds b = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 v = Vector3.Scale(m.MultiplyPoint3x4(corner), s);   // repère racine, à l'échelle réelle
                if (!any) { min = max = v; any = true; }
                else { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            }
        }
        if (!any) return false;

        center = new Vector2((min.x + max.x) * 0.5f, (min.z + max.z) * 0.5f);
        sizeXZ = new Vector2(Mathf.Max(0.5f, max.x - min.x), Mathf.Max(0.5f, max.z - min.z));
        floorOffsetY = min.y;
        return true;
    }

    /// <summary>Ajuste taille, centre et hauteur du sol sur les meshes de l'objet.</summary>
    public bool FitToGeometry()
    {
        if (!MeasureGeometry(transform, out var c, out var sz, out float fo)) return false;
        centerOffset = c;
        size = sz;
        floorOffset = fo;
        TerrainModifierManager.RequestUpdate();
        return true;
    }

    /// <summary>Hauteur monde du terrain d'ORIGINE sous l'empreinte, selon le mode.</summary>
    public bool SampleGround(FootprintGroundMode mode, out float y)
    {
        y = transform.position.y;
        if (mode == FootprintGroundMode.Center)
        {
            GetFrame(out var ctr, out _, out _);
            return TerrainModifierManager.TrySampleBaseHeight(ctr.x, ctr.y, out y);
        }

        const int n = 9;
        float sum = 0f, lo = float.MaxValue, hi = float.MinValue;
        int count = 0;
        Vector2 half = size * 0.5f;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                Vector2 f = new Vector2(Mathf.Lerp(-half.x, half.x, i / (n - 1f)), Mathf.Lerp(-half.y, half.y, j / (n - 1f)));
                Vector3 wp = FromFrame(f);
                if (!TerrainModifierManager.TrySampleBaseHeight(wp.x, wp.z, out float gy)) continue;
                sum += gy; lo = Mathf.Min(lo, gy); hi = Mathf.Max(hi, gy); count++;
            }
        }
        if (count == 0) return false;
        y = mode == FootprintGroundMode.Average ? sum / count : mode == FootprintGroundMode.Highest ? hi : lo;
        return true;
    }

    /// <summary>Descend/monte l'objet pour que le sol du bâtiment soit à la hauteur du terrain d'origine choisie.</summary>
    public bool PlaceOnGround(FootprintGroundMode mode)
    {
        if (!SampleGround(mode, out float gy)) return false;
        Vector3 p = transform.position;
        p.y = gy - floorOffset - heightOffset;
        transform.position = p;
        TerrainModifierManager.RequestUpdate();
        return true;
    }

    // ------------------------------------------------------------------
    //  Gizmos
    // ------------------------------------------------------------------
    private void OnDrawGizmos()
    {
        if (!showGizmos) return;
        Color col = ModifierMath.ColorOf(operation);
        bool sel = IsSelected();

        DrawOutline(0f, new Color(col.r, col.g, col.b, sel ? 1f : 0.7f));
        if (margin > 0.01f) DrawOutline(margin, new Color(col.r, col.g, col.b, 0.5f));
        if (blendDistance > 0.01f) DrawOutline(Reach, new Color(col.r, col.g, col.b, sel ? 0.45f : 0.2f));

        // Ligne verticale : sol visé.
        Vector3 p = transform.position;
        Gizmos.color = new Color(col.r, col.g, col.b, 0.9f);
        Gizmos.DrawLine(new Vector3(p.x, TargetY, p.z), new Vector3(p.x, ModifierMath.GroundY(p.x, p.z, TargetY), p.z));
    }

    private bool IsSelected()
    {
#if UNITY_EDITOR
        return UnityEditor.Selection.Contains(gameObject);
#else
        return false;
#endif
    }

    private void DrawOutline(float expand, Color color)
    {
        Gizmos.color = color;
        Vector2 half = size * 0.5f;
        const int arc = 6;
        Vector3 prev = default; Vector3 first = default; bool has = false;

        // 4 coins, chacun avec un quart de cercle de rayon "expand".
        for (int corner = 0; corner < 4; corner++)
        {
            Vector2 cc = new Vector2((corner == 0 || corner == 3) ? half.x : -half.x, corner < 2 ? half.y : -half.y);
            float a0 = corner * 90f;   // ordre : (+x,+z) -> (-x,+z) -> (-x,-z) -> (+x,-z)
            for (int k = 0; k <= arc; k++)
            {
                float a = (a0 + k * 90f / arc) * Mathf.Deg2Rad;
                Vector2 f = cc + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * expand;
                Vector3 wp = FromFrame(f);
                wp.y = ModifierMath.GroundY(wp.x, wp.z, TargetY) + 0.15f;
                if (has) DrawEdge(prev, wp); else { first = wp; has = true; }
                prev = wp;
            }
        }
        if (has) DrawEdge(prev, first);
    }

    // Les côtés droits sont sous-divisés pour épouser le relief.
    private static void DrawEdge(Vector3 a, Vector3 b)
    {
        float len = Vector3.Distance(a, b);
        int seg = Mathf.Clamp(Mathf.CeilToInt(len / 2f), 1, 40);
        Vector3 prev = a;
        for (int i = 1; i <= seg; i++)
        {
            Vector3 q = Vector3.Lerp(a, b, i / (float)seg);
            q.y = ModifierMath.GroundY(q.x, q.z, q.y) + 0.15f;
            Gizmos.DrawLine(prev, q);
            prev = q;
        }
    }
}