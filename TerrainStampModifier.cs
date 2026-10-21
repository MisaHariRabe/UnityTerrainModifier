using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Applique un TerrainStamp sur le terrain, comme une couche non destructive.
/// Rectangle orienté (rotation Y de l'objet), bords fondus, mêmes opérations que les autres modificateurs :
///  - Add / Subtract : relief ajouté / retiré PAR-DESSUS le terrain existant (la hauteur Y de l'objet est ignorée).
///  - Blend : le terrain prend la forme du stamp à une hauteur ABSOLUE (Y de l'objet + décalage).
///  - Raise / Lower : idem, mais on comble seulement / on creuse seulement.
/// </summary>
[ExecuteAlways]
[AddComponentMenu("Terrain/Terrain Stamp")]
public class TerrainStampModifier : MonoBehaviour, ITerrainModifier
{
    [Header("Stamp")]
    public TerrainStamp stamp;

    [Tooltip("Emprise (X = largeur, Y = profondeur) en mètres, dans le repère de l'objet tourné en Y.")]
    public Vector2 size = new Vector2(50f, 50f);

    [Tooltip("Multiplicateur de la hauteur du relief. Négatif = relief inversé (cuvette).")]
    public float heightScale = 1f;

    [Tooltip("Modes absolus (Blend / Raise / Lower) : décalage vertical par rapport à la position Y de l'objet.")]
    public float heightOffset = 0f;

    public bool flipX;
    public bool flipZ;

    [Header("Terrain")]
    public ModifierOperation operation = ModifierOperation.Add;

    [Range(0f, 1f)] public float strength = 1f;

    [Range(0f, 1f), Tooltip("Largeur du fondu sur les bords (fraction de la demi-taille). 0 = bord net.")]
    public float edgeFade = 0.25f;

    [Tooltip("Masque elliptique au lieu de rectangulaire.")]
    public bool roundMask = false;

    [Range(0, 5)] public int smoothness = 0;
    public int layerOrder = 5;

    [Header("Affichage")]
    public bool showGizmos = true;

    // ---- ITerrainModifier ----
    public Behaviour Owner => this;
    public int LayerOrder => layerOrder;
    public string Label => stamp != null ? $"{gameObject.name} ({stamp.name})" : gameObject.name;
    public ModifierOperation Operation => operation;

    /// <summary>Hauteur monde de la base du stamp (modes absolus).</summary>
    public float BaseY => transform.position.y + heightOffset;

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
        if (operation == ModifierOperation.Multiply || operation == ModifierOperation.Offset)
            operation = ModifierOperation.Add;
        TerrainModifierManager.RequestUpdate();
    }

    // ------------------------------------------------------------------
    //  Repère (rotation Y uniquement)
    // ------------------------------------------------------------------
    private void GetFrame(out Vector2 center, out Vector2 right, out Vector2 forward)
    {
        float yaw = transform.eulerAngles.y * Mathf.Deg2Rad;
        float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
        right = new Vector2(c, -s);
        forward = new Vector2(s, c);
        Vector3 p = transform.position;
        center = new Vector2(p.x, p.z);
    }

    public Bounds WorldBounds()
    {
        GetFrame(out var ctr, out _, out _);
        float r = size.magnitude * 0.5f;
        return new Bounds(new Vector3(ctr.x, transform.position.y, ctr.y), new Vector3(r * 2f, 100000f, r * 2f));
    }

    private Vector3 FromFrame(Vector2 f)
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
        if (stamp == null || !stamp.IsValid) return;

        GetFrame(out var ctr, out var right, out var fwd);
        Vector2 half = size * 0.5f;
        float bound = half.magnitude;

        if (!c.TryGetRegion(ctr - Vector2.one * bound, ctr + Vector2.one * bound,
                            out int x0, out int z0, out int w, out int h))
            return;

        float invY = 1f / c.size.y;
        float baseY = BaseY;
        float minHalf = Mathf.Min(half.x, half.y);
        float fadeM = Mathf.Max(0.0001f, edgeFade * minHalf);
        float[,] delta = new float[h, w];

        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2 d = c.PixelToWorld(x0 + x, z0 + z) - ctr;
                float nx = Vector2.Dot(d, right) / half.x;   // -1..1
                float nz = Vector2.Dot(d, fwd) / half.y;
                if (Mathf.Abs(nx) > 1f || Mathf.Abs(nz) > 1f) continue;

                // Distance (m) au bord du masque.
                float edge = roundMask
                    ? (1f - Mathf.Sqrt(nx * nx + nz * nz)) * minHalf
                    : Mathf.Min((1f - Mathf.Abs(nx)) * half.x, (1f - Mathf.Abs(nz)) * half.y);
                if (edge <= 0f) continue;

                float f = edgeFade > 0f ? ModifierMath.Smooth01(edge / fadeM) : 1f;
                float amount = strength * f;
                if (amount <= 0f) continue;

                float u = nx * 0.5f + 0.5f, v = nz * 0.5f + 0.5f;
                if (flipX) u = 1f - u;
                if (flipZ) v = 1f - v;

                float hm = stamp.Sample(u, v) * heightScale;       // mètres
                float current = c.heights[z0 + z, x0 + x];
                float target = Mathf.Clamp01((baseY + hm - c.origin.y) * invY);

                delta[z, x] = ModifierMath.Delta(operation, current, target, hm * invY, 1f, amount);
            }
        }

        c.ApplyDelta(delta, x0, z0, smoothness);
    }

    // ------------------------------------------------------------------
    //  Outils
    // ------------------------------------------------------------------
    public void ResetSizeToNative()
    {
        if (stamp != null) size = stamp.nativeSize;
        TerrainModifierManager.RequestUpdate();
    }

    /// <summary>Place la base du stamp sur le terrain d'origine, au centre (utile pour Blend / Raise / Lower).</summary>
    public bool PlaceOnGround()
    {
        Vector3 p = transform.position;
        if (!TerrainModifierManager.TrySampleBaseHeight(p.x, p.z, out float gy)) return false;
        p.y = gy - heightOffset;
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
        Vector2 half = size * 0.5f;

        DrawOutline(half, new Color(col.r, col.g, col.b, sel ? 1f : 0.6f));
        float fade = edgeFade * Mathf.Min(half.x, half.y);
        if (fade > 0.05f) DrawOutline(half - Vector2.one * fade, new Color(col.r, col.g, col.b, sel ? 0.45f : 0.2f));
    }

    private bool IsSelected()
    {
#if UNITY_EDITOR
        return UnityEditor.Selection.Contains(gameObject);
#else
        return false;
#endif
    }

    private void DrawOutline(Vector2 half, Color color)
    {
        if (half.x < 0.05f || half.y < 0.05f) return;
        Gizmos.color = color;

        var ring = new List<Vector2>();
        if (roundMask)
        {
            const int seg = 48;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                ring.Add(new Vector2(Mathf.Cos(a) * half.x, Mathf.Sin(a) * half.y));
            }
        }
        else
        {
            Vector2[] k = { new Vector2(half.x, half.y), new Vector2(-half.x, half.y),
                            new Vector2(-half.x, -half.y), new Vector2(half.x, -half.y), new Vector2(half.x, half.y) };
            for (int s = 0; s < 4; s++)
            {
                int steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(k[s], k[s + 1]) / 2f), 1, 60);
                for (int i = 0; i < steps; i++) ring.Add(Vector2.Lerp(k[s], k[s + 1], i / (float)steps));
            }
            ring.Add(k[4]);
        }

        Vector3 prev = default;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 wp = FromFrame(ring[i]);
            wp.y = ModifierMath.GroundY(wp.x, wp.z, transform.position.y) + 0.15f;
            if (i > 0) Gizmos.DrawLine(prev, wp);
            prev = wp;
        }
    }
}