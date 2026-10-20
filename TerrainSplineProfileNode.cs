using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

public enum SplineModifierMode
{
    Path,   // Bande le long de la spline (routes, rivières, crêtes)
    Shape   // Zone remplie par la spline fermée + jupe de raccord (plateformes, lacs)
}

[Serializable]
public class PointProfileHandle
{
    [Tooltip("Décalage vertical du sommet/milieu par rapport au nœud.")]
    public float peakOffsetY = 0f;

    [Tooltip("Largeur (rayon) de la base à ce point, en unités mondiales.")]
    public float baseWidth = 15f;

    [Tooltip("Position relative (0 à 1) du point de bord intermédiaire.")]
    [Range(0f, 1f)] public float edgePositionT = 0.5f;

    [Tooltip("Décalage vertical du point de bord intermédiaire.")]
    public float edgeOffsetY = 0f;

    public PointProfileHandle() { }

    public PointProfileHandle(PointProfileHandle other) { CopyFrom(other); }

    public void CopyFrom(PointProfileHandle o)
    {
        peakOffsetY = o.peakOffsetY;
        baseWidth = o.baseWidth;
        edgePositionT = o.edgePositionT;
        edgeOffsetY = o.edgeOffsetY;
    }

    /// <summary>Hauteur du profil (en mètres) à la distance normalisée normDist (0 = centre, 1 = bord).</summary>
    public static float EvaluateProfile(float peak, float edgeT, float edgeOff, float normDist, bool smooth)
    {
        edgeT = Mathf.Clamp(edgeT, 0.001f, 0.999f);
        if (normDist <= edgeT)
        {
            float t = normDist / edgeT;
            if (smooth) t = ModifierMath.Smooth01(t);
            return Mathf.Lerp(peak, edgeOff, t);
        }
        else
        {
            float t = (normDist - edgeT) / (1f - edgeT);
            if (smooth) t = ModifierMath.Smooth01(t);
            return Mathf.Lerp(edgeOff, 0f, t);
        }
    }

    public float Evaluate(float normDist, bool smooth) =>
        EvaluateProfile(peakOffsetY, edgePositionT, edgeOffsetY, normDist, smooth);
}

/// <summary>Échantillon du chemin avec le profil interpolé (coordonnées monde).</summary>
public struct ProfileSample
{
    public Vector3 position;
    public float peakOffsetY, baseWidth, edgeT, edgeOffsetY;
    public float knotU; // position en "unités de nœud" (1.5 = mi-chemin entre nœud 1 et 2)
}

[ExecuteAlways]
[RequireComponent(typeof(SplineContainer))]
[AddComponentMenu("Terrain/Terrain Spline Profile Node")]
public class TerrainSplineProfileNode : MonoBehaviour, ITerrainModifier
{
    [Header("Général")]
    public SplineModifierMode mode = SplineModifierMode.Path;
    public ModifierOperation operation = ModifierOperation.Blend;

    [Range(0f, 1f)] public float strength = 1f;
    [Range(0, 5)] public int smoothness = 1;
    public int layerOrder = 0;

    [Header("Profil")]
    [Tooltip("Adoucit les transitions du profil (courbe en S).")]
    public bool smoothProfile = true;

    [Range(0f, 1f)]
    [Tooltip("Part de la largeur (côté extérieur) utilisée pour fondre l'effet dans le terrain existant.")]
    public float edgeFade = 0.3f;

    [Tooltip("Mode Shape : l'intérieur est parfaitement plat (hauteur moyenne des nœuds).")]
    public bool flatInterior = true;

    [Range(4, 128)]
    [Tooltip("Nombre d'échantillons par segment de spline (précision vs performance).")]
    public int samplesPerCurve = 16;

    [Header("Affichage")]
    public bool showGizmos = true;

    [HideInInspector]
    public List<PointProfileHandle> pointHandles = new List<PointProfileHandle>();

    private SplineContainer splineContainer;
    private readonly List<ProfileSample> samples = new List<ProfileSample>();
    private readonly List<ProfileSample> gizmoSamples = new List<ProfileSample>();

    // ---- ITerrainModifier ----
    public Behaviour Owner => this;
    public int LayerOrder => layerOrder;
    public string Label => gameObject.name;
    public ModifierOperation Operation => operation;

    public SplineContainer GetSplineContainer()
    {
        if (splineContainer == null) splineContainer = GetComponent<SplineContainer>();
        return splineContainer;
    }

    // ---------- Cycle de vie ----------
    private void OnEnable()
    {
        GetSplineContainer();
        SyncHandlesWithSpline();
        Spline.Changed += OnSplineChanged;
        transform.hasChanged = false;
        TerrainModifierManager.Register(this);
    }

    private void OnDisable()
    {
        Spline.Changed -= OnSplineChanged;
        TerrainModifierManager.Unregister(this);
    }

    private void Reset() => SyncHandlesWithSpline();

    private void Update()
    {
        if (transform.hasChanged)
        {
            transform.hasChanged = false;
            TerrainModifierManager.RequestUpdate();
        }
    }

    private void OnValidate()
    {
        SyncHandlesWithSpline();
        TerrainModifierManager.RequestUpdate();
    }

    // ---------- Synchronisation des handles avec les nœuds ----------
    public void SyncHandlesWithSpline()
    {
        var c = GetSplineContainer();
        if (c == null || c.Spline == null) return;

        int knotCount = c.Spline.Count;
        while (pointHandles.Count < knotCount)
            pointHandles.Add(pointHandles.Count > 0 ? new PointProfileHandle(pointHandles[pointHandles.Count - 1]) : new PointProfileHandle());
        while (pointHandles.Count > knotCount)
            pointHandles.RemoveAt(pointHandles.Count - 1);
    }

    private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
    {
        var c = GetSplineContainer();
        if (c == null || spline != c.Spline) return;

        // Insertion/suppression au bon index pour garder les profils alignés sur leurs nœuds.
        if (modification == SplineModification.KnotInserted && pointHandles.Count < spline.Count
            && knotIndex >= 0 && knotIndex <= pointHandles.Count)
        {
            int src = Mathf.Clamp(knotIndex - 1, 0, Mathf.Max(0, pointHandles.Count - 1));
            pointHandles.Insert(knotIndex, pointHandles.Count > 0 ? new PointProfileHandle(pointHandles[src]) : new PointProfileHandle());
        }
        else if (modification == SplineModification.KnotRemoved && pointHandles.Count > spline.Count
                 && knotIndex >= 0 && knotIndex < pointHandles.Count)
        {
            pointHandles.RemoveAt(knotIndex);
        }

        SyncHandlesWithSpline();
        TerrainModifierManager.RequestUpdate();
    }

    // ---------- Géométrie ----------
    /// <summary>Position monde du nœud i et vecteur "droite" (horizontal, perpendiculaire à la tangente).</summary>
    public bool TryGetKnotFrame(int i, out Vector3 worldPos, out Vector3 right)
    {
        worldPos = Vector3.zero; right = transform.right;
        var c = GetSplineContainer();
        if (c == null || c.Spline == null) return false;
        Spline sp = c.Spline;
        int count = sp.Count;
        if (i < 0 || i >= count) return false;

        worldPos = transform.TransformPoint((Vector3)sp[i].Position);
        if (count < 2) return true;

        int curves = sp.Closed ? count : count - 1;
        Vector3 tangentLocal;
        if (i < curves) tangentLocal = (Vector3)CurveUtility.EvaluateTangent(sp.GetCurve(i), 0f);
        else tangentLocal = (Vector3)CurveUtility.EvaluateTangent(sp.GetCurve(i - 1), 1f);

        Vector3 t = transform.TransformVector(tangentLocal);
        t.y = 0f;
        if (t.sqrMagnitude < 1e-6f)
        {
            // Tangente dégénérée : on utilise la direction vers le voisin.
            Vector3 n = transform.TransformPoint((Vector3)sp[(i + 1) % count].Position) - worldPos;
            n.y = 0f; t = n;
        }
        if (t.sqrMagnitude > 1e-6f) right = Vector3.Cross(Vector3.up, t).normalized;
        return true;
    }

    private static ProfileSample MakeSample(Vector3 pos, PointProfileHandle a, PointProfileHandle b, float t, float u)
    {
        return new ProfileSample
        {
            position = pos,
            peakOffsetY = Mathf.Lerp(a.peakOffsetY, b.peakOffsetY, t),
            baseWidth = Mathf.Max(0.01f, Mathf.Lerp(a.baseWidth, b.baseWidth, t)),
            edgeT = Mathf.Lerp(a.edgePositionT, b.edgePositionT, t),
            edgeOffsetY = Mathf.Lerp(a.edgeOffsetY, b.edgeOffsetY, t),
            knotU = u
        };
    }

    /// <summary>Discrétise la spline en polyligne monde avec profil interpolé. loop = polygone fermé.</summary>
    public bool BuildSamples(List<ProfileSample> result, out bool loop)
    {
        result.Clear();
        loop = false;

        var c = GetSplineContainer();
        if (c == null || c.Spline == null) return false;
        Spline sp = c.Spline;
        int count = sp.Count;
        if (count < 2 || (mode == SplineModifierMode.Shape && count < 3)) return false;

        SyncHandlesWithSpline();

        bool closed = sp.Closed;
        loop = closed || mode == SplineModifierMode.Shape;
        int curves = closed ? count : count - 1;
        int per = Mathf.Max(2, samplesPerCurve);

        for (int i = 0; i < curves; i++)
        {
            BezierCurve curve = sp.GetCurve(i);
            PointProfileHandle a = pointHandles[i];
            PointProfileHandle b = pointHandles[(i + 1) % count];
            for (int s = 0; s < per; s++)
            {
                float t = s / (float)per;
                Vector3 local = (Vector3)CurveUtility.EvaluatePosition(curve, t);
                result.Add(MakeSample(transform.TransformPoint(local), a, b, t, i + t));
            }
        }

        if (!closed)
        {
            PointProfileHandle last = pointHandles[count - 1];
            result.Add(MakeSample(transform.TransformPoint((Vector3)sp[count - 1].Position), last, last, 0f, count - 1));
        }
        return result.Count >= 2;
    }

    // ---------- Application sur le terrain ----------
    public void Apply(TerrainHeightContext c)
    {
        if (!BuildSamples(samples, out bool loop)) return;

        int n = samples.Count;
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        float maxW = 0f, meanY = 0f, meanPeak = 0f;
        for (int i = 0; i < n; i++)
        {
            meanPeak += samples[i].peakOffsetY;
            Vector3 p = samples[i].position;
            min = Vector2.Min(min, new Vector2(p.x, p.z));
            max = Vector2.Max(max, new Vector2(p.x, p.z));
            maxW = Mathf.Max(maxW, samples[i].baseWidth);
            meanY += p.y;
        }
        meanY /= n;
        meanPeak /= n;

        if (!c.TryGetRegion(min - Vector2.one * maxW, max + Vector2.one * maxW,
                            out int rx0, out int rz0, out int rw, out int rh))
            return;

        int cells = rw * rh;
        var bestSq = new float[cells];
        var bestSeg = new int[cells];
        var bestF = new float[cells];
        for (int i = 0; i < cells; i++) bestSq[i] = float.MaxValue;

        // 1) Segment le plus proche de chaque pixel (rastérisation par segment => pas de boucle pixel x nœuds).
        int segCount = loop ? n : n - 1;
        for (int k = 0; k < segCount; k++)
        {
            ProfileSample sa = samples[k], sb = samples[(k + 1) % n];
            Vector2 A = new Vector2(sa.position.x, sa.position.z);
            Vector2 B = new Vector2(sb.position.x, sb.position.z);
            float w = Mathf.Max(sa.baseWidth, sb.baseWidth);

            if (!c.TryGetRegion(Vector2.Min(A, B) - Vector2.one * w, Vector2.Max(A, B) + Vector2.one * w,
                                out int sx0, out int sz0, out int sw, out int sh))
                continue;

            Vector2 ab = B - A;
            float abLenSq = ab.sqrMagnitude;

            for (int z = sz0; z < sz0 + sh; z++)
            {
                for (int x = sx0; x < sx0 + sw; x++)
                {
                    Vector2 P = c.PixelToWorld(x, z);
                    float f = abLenSq > 1e-8f ? Mathf.Clamp01(Vector2.Dot(P - A, ab) / abLenSq) : 0f;
                    float dSq = (P - (A + ab * f)).sqrMagnitude;
                    int idx = (z - rz0) * rw + (x - rx0);
                    if (dSq < bestSq[idx]) { bestSq[idx] = dSq; bestSeg[idx] = k; bestF[idx] = f; }
                }
            }
        }

        // 2) Mode Shape : remplissage de l'intérieur du polygone par balayage de lignes.
        bool[] inside = null;
        if (mode == SplineModifierMode.Shape)
        {
            inside = new bool[cells];
            var xs = new List<float>(16);
            for (int r = 0; r < rh; r++)
            {
                float wz = c.origin.z + (rz0 + r) * c.cellZ;
                xs.Clear();
                for (int k = 0; k < n; k++)
                {
                    Vector3 a = samples[k].position, b = samples[(k + 1) % n].position;
                    if ((a.z <= wz) != (b.z <= wz))
                        xs.Add(a.x + (wz - a.z) / (b.z - a.z) * (b.x - a.x));
                }
                xs.Sort();
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    int px0 = Mathf.Max(rx0, Mathf.CeilToInt((xs[i] - c.origin.x) / c.cellX));
                    int px1 = Mathf.Min(rx0 + rw - 1, Mathf.FloorToInt((xs[i + 1] - c.origin.x) / c.cellX));
                    for (int px = px0; px <= px1; px++) inside[r * rw + (px - rx0)] = true;
                }
            }
        }

        // 3) Profil -> delta.
        float[,] delta = new float[rh, rw];
        float invY = 1f / c.size.y;
        bool shapeFlat = mode == SplineModifierMode.Shape && flatInterior;

        for (int idx = 0; idx < cells; idx++)
        {
            bool ins = inside != null && inside[idx];
            bool hasSeg = bestSq[idx] != float.MaxValue;
            if (!hasSeg && !ins) continue;

            float profile, baseY, weight;
            if (hasSeg)
            {
                ProfileSample a = samples[bestSeg[idx]];
                ProfileSample b = samples[(bestSeg[idx] + 1) % n];
                float f = bestF[idx];

                float width = Mathf.Max(0.01f, Mathf.Lerp(a.baseWidth, b.baseWidth, f));
                float nd = ins ? 0f : Mathf.Sqrt(bestSq[idx]) / width;
                if (nd > 1f) continue;

                profile = PointProfileHandle.EvaluateProfile(
                    Mathf.Lerp(a.peakOffsetY, b.peakOffsetY, f),
                    Mathf.Lerp(a.edgeT, b.edgeT, f),
                    Mathf.Lerp(a.edgeOffsetY, b.edgeOffsetY, f),
                    nd, smoothProfile);

                baseY = shapeFlat ? meanY : Mathf.Lerp(a.position.y, b.position.y, f);
                weight = edgeFade > 0f ? 1f - ModifierMath.Smooth01((nd - (1f - edgeFade)) / edgeFade) : 1f;
            }
            else
            {
                // Cœur de la shape : trop loin du contour pour avoir un segment proche.
                profile = meanPeak;
                weight = 1f;
                if (shapeFlat) baseY = meanY;
                else
                {
                    // Hauteur de l'échantillon de contour le plus proche.
                    Vector2 P = c.PixelToWorld(rx0 + idx % rw, rz0 + idx / rw);
                    float best = float.MaxValue; baseY = meanY;
                    for (int s = 0; s < n; s++)
                    {
                        Vector3 sp = samples[s].position;
                        float d = (new Vector2(sp.x, sp.z) - P).sqrMagnitude;
                        if (d < best) { best = d; baseY = sp.y; }
                    }
                }
            }

            float amount = strength * weight;
            if (amount <= 0f) continue;

            int lx = idx % rw, lz = idx / rw;
            float current = c.heights[rz0 + lz, rx0 + lx];
            float targetNorm = Mathf.Clamp01((baseY + profile - c.origin.y) * invY);

            delta[lz, lx] = ModifierMath.Delta(operation, current, targetNorm, profile * invY, profile * 0.1f, amount);
        }

        c.ApplyDelta(delta, rx0, rz0, smoothness);
    }

    // ---------- Gizmos ----------
    private void OnDrawGizmos()
    {
        if (!showGizmos) return;
        if (!BuildSamples(gizmoSamples, out bool loop)) return;

        Color col = ModifierMath.ColorOf(operation);
        int n = gizmoSamples.Count;
        int segs = loop ? n : n - 1;

        for (int k = 0; k < segs; k++)
        {
            ProfileSample a = gizmoSamples[k], b = gizmoSamples[(k + 1) % n];
            Vector3 dir = b.position - a.position; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f) continue;
            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;

            Gizmos.color = new Color(col.r, col.g, col.b, 0.9f);
            Gizmos.DrawLine(a.position + Vector3.up * a.peakOffsetY, b.position + Vector3.up * b.peakOffsetY);

            Gizmos.color = new Color(col.r, col.g, col.b, 0.45f);
            Gizmos.DrawLine(a.position + right * a.baseWidth, b.position + right * b.baseWidth);
            Gizmos.DrawLine(a.position - right * a.baseWidth, b.position - right * b.baseWidth);
        }
    }
}