using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>Compression gzip des grosses grilles d'octets (masques, base) pour garder la scène légère.</summary>
public static class PaintPack
{
    public static byte[] Pack(byte[] raw)
    {
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }
    }

    public static byte[] Unpack(byte[] packed, int expected)
    {
        var res = new byte[Mathf.Max(0, expected)];
        if (packed == null || packed.Length == 0 || expected <= 0) return res;
        try
        {
            using (var ms = new MemoryStream(packed))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            {
                int off = 0;
                while (off < expected)
                {
                    int r = gz.Read(res, off, expected - off);
                    if (r <= 0) break;
                    off += r;
                }
            }
        }
        catch (Exception) { /* données corrompues : masque vide */ }
        return res;
    }
}

/// <summary>
/// Un calque de peinture : une TerrainLayer (texture) + un MASQUE peint (0..255) qui dit où elle apparaît.
/// Le masque vit dans un repère propre au calque (décalage X/Z, rotation, échelle) :
/// bouger / tourner / agrandir le calque déplace donc toute la "projection" peinte.
/// </summary>
[Serializable]
public class PaintLayer : ISerializationCallbackReceiver
{
    public string name = "Calque";
    public bool visible = true;
    [Range(0f, 1f)] public float opacity = 1f;
    public TerrainLayer terrainLayer;

    [Header("Transformation du masque")]
    [Tooltip("Décalage (mètres, X/Z) depuis le centre du terrain.")]
    public Vector2 offset = Vector2.zero;
    [Tooltip("Rotation autour du centre du calque (degrés).")]
    public float rotation = 0f;
    public Vector2 scale = Vector2.one;

    [Header("Masque (fixé à la création du calque)")]
    public int maskRes = 1024;
    [Tooltip("Côté du carré du masque, en mètres, à l'échelle 1.")]
    public float maskSize = 1500f;

    [SerializeField, HideInInspector] private byte[] packed;
    [NonSerialized] public byte[] mask;
    [NonSerialized] private bool packedStale;

    // ---------- Sérialisation : le masque est stocké compressé ----------
    public void OnBeforeSerialize()
    {
        if (mask != null && (packedStale || packed == null))
        {
            packed = PaintPack.Pack(mask);
            packedStale = false;
        }
    }

    public void OnAfterDeserialize()
    {
        mask = (packed != null && packed.Length > 0) ? PaintPack.Unpack(packed, maskRes * maskRes) : null;
        packedStale = false;
    }

    public void EnsureMask()
    {
        int n = maskRes * maskRes;
        if (mask == null || mask.Length != n) { mask = new byte[n]; packedStale = true; }
    }

    public void SetAll(byte v)
    {
        EnsureMask();
        for (int i = 0; i < mask.Length; i++) mask[i] = v;
        packedStale = true;
    }

    public PaintLayer Clone()
    {
        var c = new PaintLayer
        {
            name = name + " copie", visible = visible, opacity = opacity, terrainLayer = terrainLayer,
            offset = offset, rotation = rotation, scale = scale, maskRes = maskRes, maskSize = maskSize
        };
        if (mask != null) { c.mask = (byte[])mask.Clone(); c.packedStale = true; }
        return c;
    }

    // ---------- Repère ----------
    public struct Frame
    {
        public Vector2 pivot;      // centre du calque (monde XZ)
        public float cos, sin;
        public Vector2 scale;
        public float size;
        public int res;
    }

    public Frame GetFrame(Vector3 terrainOrigin, Vector3 terrainSize)
    {
        float r = rotation * Mathf.Deg2Rad;
        return new Frame
        {
            pivot = new Vector2(terrainOrigin.x + terrainSize.x * 0.5f + offset.x,
                                terrainOrigin.z + terrainSize.z * 0.5f + offset.y),
            cos = Mathf.Cos(r), sin = Mathf.Sin(r),
            scale = new Vector2(Mathf.Max(0.01f, scale.x), Mathf.Max(0.01f, scale.y)),
            size = maskSize, res = maskRes
        };
    }

    /// <summary>Point monde (XZ) -> coordonnées pixel du masque (flottantes).</summary>
    public static Vector2 WorldToPixel(in Frame f, Vector2 w)
    {
        Vector2 d = w - f.pivot;
        float lx = (f.cos * d.x + f.sin * d.y) / f.scale.x;
        float lz = (-f.sin * d.x + f.cos * d.y) / f.scale.y;
        float last = f.res - 1;
        return new Vector2((lx / f.size + 0.5f) * last, (lz / f.size + 0.5f) * last);
    }

    /// <summary>Pixel du masque -> point monde (XZ).</summary>
    public static Vector2 PixelToWorld(in Frame f, float px, float py)
    {
        float last = f.res - 1;
        float lx = (px / last - 0.5f) * f.size * f.scale.x;
        float lz = (py / last - 0.5f) * f.size * f.scale.y;
        return new Vector2(f.pivot.x + f.cos * lx - f.sin * lz, f.pivot.y + f.sin * lx + f.cos * lz);
    }

    /// <summary>Valeur du masque (0..1, bilinéaire) en un point monde. 0 hors du carré du masque.</summary>
    public float Sample(in Frame f, Vector2 world)
    {
        if (mask == null || mask.Length < f.res * f.res) return 0f;
        Vector2 p = WorldToPixel(in f, world);
        int last = f.res - 1;
        if (p.x < 0f || p.y < 0f || p.x > last || p.y > last) return 0f;
        int x = Mathf.Min((int)p.x, last - 1), y = Mathf.Min((int)p.y, last - 1);
        float tx = p.x - x, ty = p.y - y;
        int i = y * f.res + x;
        float a = Mathf.Lerp(mask[i], mask[i + 1], tx);
        float b = Mathf.Lerp(mask[i + f.res], mask[i + f.res + 1], tx);
        return Mathf.Lerp(a, b, ty) / 255f;
    }

    // ---------- Peinture ----------
    /// <summary>Un coup de pinceau rond (rayon en mètres, dans le monde). Renvoie la boîte monde touchée.</summary>
    public void Stamp(in Frame f, Vector2 center, float radius, float hardness, float strength, bool erase,
                      out Vector2 minW, out Vector2 maxW)
    {
        minW = center - Vector2.one * radius;
        maxW = center + Vector2.one * radius;
        EnsureMask();

        int res = f.res, last = res - 1;
        Vector2 pc = WorldToPixel(in f, center);
        float pixW = f.size / last * Mathf.Min(f.scale.x, f.scale.y);   // plus petite taille monde d'un pixel
        int rp = Mathf.CeilToInt(radius / Mathf.Max(0.0001f, pixW)) + 1;

        int x0 = Mathf.Max(0, Mathf.FloorToInt(pc.x) - rp), x1 = Mathf.Min(last, Mathf.CeilToInt(pc.x) + rp);
        int y0 = Mathf.Max(0, Mathf.FloorToInt(pc.y) - rp), y1 = Mathf.Min(last, Mathf.CeilToInt(pc.y) + rp);
        float hard = Mathf.Clamp01(hardness);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Vector2 w = PixelToWorld(in f, x, y);
                float d = (w - center).magnitude;
                if (d >= radius) continue;

                float t = d / radius;
                float fall = t <= hard ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (t - hard) / Mathf.Max(0.0001f, 1f - hard));
                float a = fall * strength;
                if (a <= 0f) continue;

                int i = y * res + x;
                float m = mask[i] / 255f;
                m = erase ? m * (1f - a) : m + (1f - m) * a;
                mask[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(m) * 255f);
            }
        }
        packedStale = true;
    }

    /// <summary>Remplit (ou efface) l'intérieur d'un contour libre (points monde XZ), avec un bord adouci (feather, en mètres).</summary>
    public void FillPolygon(in Frame f, IList<Vector2> worldPts, float featherMeters, float strength, bool erase,
                            out Vector2 minW, out Vector2 maxW)
    {
        minW = maxW = Vector2.zero;
        int n = worldPts.Count;
        if (n < 3) return;
        EnsureMask();

        int res = f.res, last = res - 1;
        var pp = new Vector2[n];
        float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            pp[i] = WorldToPixel(in f, worldPts[i]);
            minx = Mathf.Min(minx, pp[i].x); maxx = Mathf.Max(maxx, pp[i].x);
            miny = Mathf.Min(miny, pp[i].y); maxy = Mathf.Max(maxy, pp[i].y);
        }

        float pixW = f.size / last * (f.scale.x + f.scale.y) * 0.5f;
        int fr = Mathf.Clamp(Mathf.RoundToInt(featherMeters / Mathf.Max(0.0001f, pixW)), 0, 100);
        int pad = fr * 2 + 1;   // le flou (2 passes) déborde de 2 x fr

        int bx0 = Mathf.Max(0, Mathf.FloorToInt(minx) - pad), bx1 = Mathf.Min(last, Mathf.CeilToInt(maxx) + pad);
        int by0 = Mathf.Max(0, Mathf.FloorToInt(miny) - pad), by1 = Mathf.Min(last, Mathf.CeilToInt(maxy) + pad);
        if (bx1 < bx0 || by1 < by0) return;

        int w = bx1 - bx0 + 1, h = by1 - by0 + 1;
        var cov = new float[w * h];
        var xs = new List<float>(16);

        // Remplissage par balayage de lignes (règle pair / impair).
        for (int y = by0; y <= by1; y++)
        {
            xs.Clear();
            for (int i = 0; i < n; i++)
            {
                Vector2 a = pp[i], b = pp[(i + 1) % n];
                if ((a.y <= y) != (b.y <= y))
                    xs.Add(a.x + (y - a.y) / (b.y - a.y) * (b.x - a.x));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xa = Mathf.Max(bx0, Mathf.CeilToInt(xs[k]));
                int xb = Mathf.Min(bx1, Mathf.FloorToInt(xs[k + 1]));
                for (int x = xa; x <= xb; x++) cov[(y - by0) * w + (x - bx0)] = 1f;
            }
        }

        if (fr > 0)
        {
            var tmp = new float[cov.Length];
            for (int pass = 0; pass < 2; pass++) BoxBlur(cov, tmp, w, h, fr);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float c = cov[y * w + x] * strength;
                if (c <= 0f) continue;
                int i = (by0 + y) * res + (bx0 + x);
                float m = mask[i] / 255f;
                m = erase ? m * (1f - c) : m + (1f - m) * c;
                mask[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(m) * 255f);
            }
        }
        packedStale = true;

        Vector2 c0 = PixelToWorld(in f, bx0, by0), c1 = PixelToWorld(in f, bx1, by0);
        Vector2 c2 = PixelToWorld(in f, bx1, by1), c3 = PixelToWorld(in f, bx0, by1);
        minW = Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3));
        maxW = Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3));
    }

    /// <summary>Flou de boîte séparable (fenêtre glissante, O(n) quel que soit le rayon).</summary>
    private static void BoxBlur(float[] a, float[] tmp, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0f;
            for (int k = -r; k <= r; k++) if (k >= 0 && k < w) sum += a[row + k];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                int add = x + r + 1, rem = x - r;
                if (add < w) sum += a[row + add];
                if (rem >= 0) sum -= a[row + rem];
            }
        }

        for (int x = 0; x < w; x++)
        {
            float sum = 0f;
            for (int k = -r; k <= r; k++) if (k >= 0 && k < h) sum += tmp[k * w + x];
            for (int y = 0; y < h; y++)
            {
                a[y * w + x] = sum * inv;
                int add = y + r + 1, rem = y - r;
                if (add < h) sum += tmp[add * w + x];
                if (rem >= 0) sum -= tmp[rem * w + x];
            }
        }
    }
}

/// <summary>
/// À placer sur le Terrain. Conserve une copie "base" des alphamaps (la peinture d'origine) et recompose
/// base + calques (du bas vers le haut) de façon non destructive. Cacher / déplacer / réordonner un calque
/// ou supprimer ce composant redonne le terrain d'origine.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Terrain))]
[AddComponentMenu("Terrain/Terrain Paint Manager")]
public class TerrainPaintManager : MonoBehaviour, ISerializationCallbackReceiver
{
    [Tooltip("Recompose automatiquement le terrain quand un calque change.")]
    public bool autoUpdate = true;

    /// <summary>Index 0 = calque du BAS ; le dernier est au-dessus de tous les autres.</summary>
    [HideInInspector] public List<PaintLayer> layers = new List<PaintLayer>();

    [SerializeField, HideInInspector] private byte[] basePacked;
    [SerializeField, HideInInspector] private int baseRes;
    [SerializeField, HideInInspector] private int baseChannels;
    [NonSerialized] private byte[] baseAlpha;
    [NonSerialized] private bool baseStale;

    private Terrain terrain;
    private TerrainData terrainData;
    private bool dirty, scheduled;

    public bool HasBase => baseAlpha != null && baseRes > 0;
    public float BaseMemoryMB => baseAlpha != null ? baseAlpha.Length / (1024f * 1024f) : 0f;
    public int TerrainChannelCount => terrainData != null ? terrainData.alphamapLayers : (GetTerrainData() != null ? terrainData.alphamapLayers : 0);
    public Vector3 Origin => transform.position;
    public Vector3 Size { get { var d = GetTerrainData(); return d != null ? d.size : Vector3.one; } }

    private TerrainData GetTerrainData()
    {
        if (terrain == null) terrain = GetComponent<Terrain>();
        terrainData = terrain != null ? terrain.terrainData : null;
        return terrainData;
    }

    public bool ContainsXZ(Vector3 w)
    {
        var d = GetTerrainData();
        if (d == null) return false;
        Vector3 o = transform.position;
        return w.x >= o.x && w.z >= o.z && w.x <= o.x + d.size.x && w.z <= o.z + d.size.z;
    }

    // ---------- Sérialisation de la base (compressée) ----------
    public void OnBeforeSerialize()
    {
        if (baseAlpha != null && (baseStale || basePacked == null))
        {
            basePacked = PaintPack.Pack(baseAlpha);
            baseStale = false;
        }
    }

    public void OnAfterDeserialize()
    {
        baseAlpha = (basePacked != null && baseRes > 0) ? PaintPack.Unpack(basePacked, baseRes * baseRes * baseChannels) : null;
        baseStale = false;
    }

    // ---------- Cycle de vie ----------
    private void OnEnable()
    {
#if UNITY_EDITOR
        Undo.undoRedoPerformed += OnUndoRedo;
#endif
        if (EnsureReady()) MarkDirty();
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        Undo.undoRedoPerformed -= OnUndoRedo;
#endif
    }

    private void LateUpdate()
    {
        if (!dirty) return;
        dirty = false;
        RecomposeAll();
    }

#if UNITY_EDITOR
    private void OnUndoRedo() => MarkDirty();
    private void OnValidate() => MarkDirty();
#endif

    public void MarkDirty()
    {
        if (!autoUpdate) return;
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
        RecomposeAll();
    }
#endif

    // ---------- Base ----------
    private bool EnsureReady()
    {
        if (GetTerrainData() == null) return false;
        int res = terrainData.alphamapResolution;
        if (!HasBase || baseRes != res)
        {
            if (HasBase)
                Debug.LogWarning($"[TerrainPaintManager] La résolution des alphamaps a changé ({baseRes} → {res}) : la base est recapturée (les calques restent).", this);
            CaptureBase();
        }
        return true;
    }

    /// <summary>Capture la peinture actuelle du terrain comme nouvelle base.</summary>
    public void CaptureBase()
    {
        if (GetTerrainData() == null) return;
        int res = terrainData.alphamapResolution;
        int ch = terrainData.alphamapLayers;

        baseAlpha = new byte[res * res * ch];
        if (ch > 0)
        {
            float[,,] a = terrainData.GetAlphamaps(0, 0, res, res);   // [z, x, couche]
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    int bi = (z * res + x) * ch;
                    for (int c = 0; c < ch; c++) baseAlpha[bi + c] = (byte)Mathf.RoundToInt(Mathf.Clamp01(a[z, x, c]) * 255f);
                }
        }
        baseRes = res;
        baseChannels = ch;
        baseStale = true;
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
    }

    /// <summary>Réécrit la peinture d'origine dans le terrain (sans calques).</summary>
    public void RestoreBase()
    {
        if (!EnsureReady()) return;
        int res = baseRes, ch = terrainData.alphamapLayers;
        if (ch == 0) return;

        var a = new float[res, res, ch];
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                int bi = (z * res + x) * baseChannels;
                float sum = 0f;
                for (int c = 0; c < ch; c++)
                {
                    float v = c < baseChannels ? baseAlpha[bi + c] / 255f : 0f;
                    a[z, x, c] = v; sum += v;
                }
                if (sum < 1e-4f) a[z, x, 0] = 1f;
            }
        terrainData.SetAlphamaps(0, 0, a);
        MarkAssetDirty();
    }

    /// <summary>Intègre les calques dans la base puis les supprime.</summary>
    public void Bake()
    {
        RecomposeAll();
        CaptureBase();
        layers.Clear();
    }

    // ---------- Calques ----------
    public PaintLayer AddLayer(TerrainLayer tl, string layerName)
    {
        if (!EnsureReady()) return null;   // capture la base AVANT d'ajouter des couches au terrain
        var l = new PaintLayer
        {
            name = layerName,
            terrainLayer = tl,
            maskSize = Mathf.Max(terrainData.size.x, terrainData.size.z) * 1.5f   // marge pour pouvoir décaler le calque
        };
        l.EnsureMask();
        layers.Add(l);
        EnsureTerrainLayers();
        MarkDirty();
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
        return l;
    }

    /// <summary>Ajoute à la TerrainData les TerrainLayer utilisées par les calques (chacune devient un canal d'alphamap).</summary>
    public void EnsureTerrainLayers()
    {
        if (GetTerrainData() == null) return;
        var list = new List<TerrainLayer>(terrainData.terrainLayers);
        bool changed = false;
        foreach (var l in layers)
            if (l != null && l.terrainLayer != null && !list.Contains(l.terrainLayer)) { list.Add(l.terrainLayer); changed = true; }
        if (changed)
        {
            terrainData.terrainLayers = list.ToArray();
            MarkAssetDirty();
        }
    }

    private int ChannelOf(TerrainLayer tl)
    {
        if (tl == null) return -1;
        var arr = terrainData.terrainLayers;
        for (int i = 0; i < arr.Length; i++) if (arr[i] == tl) return i;
        return -1;
    }

    // ---------- Composition ----------
    private struct Active
    {
        public PaintLayer layer;
        public PaintLayer.Frame frame;
        public int channel;
        public float opacity;
    }

    public void RecomposeAll()
    {
        if (!EnsureReady()) return;
        Recompose(0, 0, baseRes, baseRes);
    }

    /// <summary>Recompose seulement la zone monde (XZ) donnée : utilisé pendant la peinture.</summary>
    public void RecomposeWorldRect(Vector2 minW, Vector2 maxW)
    {
        if (!EnsureReady()) return;
        int res = baseRes;
        Vector3 o = transform.position, s = terrainData.size;
        int x0 = Mathf.Clamp(Mathf.FloorToInt((minW.x - o.x) / s.x * res) - 1, 0, res - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt((maxW.x - o.x) / s.x * res) + 1, 0, res - 1);
        int z0 = Mathf.Clamp(Mathf.FloorToInt((minW.y - o.z) / s.z * res) - 1, 0, res - 1);
        int z1 = Mathf.Clamp(Mathf.CeilToInt((maxW.y - o.z) / s.z * res) + 1, 0, res - 1);
        if (x1 < x0 || z1 < z0) return;
        Recompose(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
    }

    private void Recompose(int x0, int z0, int w, int h)
    {
        if (!EnsureReady()) return;
        EnsureTerrainLayers();

        int res = baseRes;
        int ch = terrainData.alphamapLayers;
        if (ch == 0) return;
        w = Mathf.Min(w, res - x0);
        h = Mathf.Min(h, res - z0);
        if (w <= 0 || h <= 0) return;

        Vector3 o = transform.position, s = terrainData.size;

        var act = new List<Active>();
        foreach (var l in layers)   // du bas vers le haut
        {
            if (l == null || !l.visible || l.opacity <= 0f || l.mask == null) continue;
            int c = ChannelOf(l.terrainLayer);
            if (c < 0) continue;
            act.Add(new Active { layer = l, frame = l.GetFrame(o, s), channel = c, opacity = l.opacity });
        }

        var result = new float[h, w, ch];
        var wt = new float[ch];

        for (int z = 0; z < h; z++)
        {
            int gz = z0 + z;
            for (int x = 0; x < w; x++)
            {
                int gx = x0 + x;
                int bi = (gz * res + gx) * baseChannels;
                for (int c = 0; c < ch; c++) wt[c] = c < baseChannels ? baseAlpha[bi + c] / 255f : 0f;

                if (act.Count > 0)
                {
                    var world = new Vector2(o.x + (gx + 0.5f) / res * s.x, o.z + (gz + 0.5f) / res * s.z);
                    for (int i = 0; i < act.Count; i++)
                    {
                        Active a = act[i];
                        float m = a.layer.Sample(in a.frame, world) * a.opacity;
                        if (m <= 0f) continue;
                        for (int c = 0; c < ch; c++) wt[c] *= 1f - m;
                        wt[a.channel] += m;
                    }
                }

                float sum = 0f;
                for (int c = 0; c < ch; c++) sum += wt[c];
                if (sum < 1e-4f) { for (int c = 0; c < ch; c++) wt[c] = 0f; wt[0] = 1f; sum = 1f; }
                float inv = 1f / sum;
                for (int c = 0; c < ch; c++) result[z, x, c] = wt[c] * inv;
            }
        }

        terrainData.SetAlphamaps(x0, z0, result);
        MarkAssetDirty();
    }

    private void MarkAssetDirty()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && terrainData != null) EditorUtility.SetDirty(terrainData);
#endif
    }
}