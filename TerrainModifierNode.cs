using UnityEngine;

/// <summary>
/// Modificateur circulaire : la hauteur du nœud (position Y) pilote l'effet.
/// </summary>
[ExecuteAlways]
[AddComponentMenu("Terrain/Terrain Modifier Node")]
public class TerrainModifierNode : MonoBehaviour, ITerrainModifier
{
    [Header("Paramètres du Sommet")]
    [Tooltip("Rayon d'action de la modification en unités mondiales.")]
    [Min(0.5f)] public float radius = 15f;

    [Range(0f, 1f)]
    [Tooltip("Multiplicateur d'intensité (0 à 1).")]
    public float strength = 1f;

    [Range(0, 5)]
    [Tooltip("Nombre de passes de lissage pour adoucir les pentes.")]
    public int smoothness = 1;

    [Tooltip("Type d'opération non destructive.")]
    public ModifierOperation operation = ModifierOperation.Add;

    [Tooltip("Courbe de décroissance du centre (t=0) vers le bord (t=1).")]
    public AnimationCurve falloffCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

    [Tooltip("Ordre de la couche (les couches inférieures sont calculées en premier).")]
    public int layerOrder = 0;

    [Header("Affichage")]
    public bool showGizmos = true;

    // ---- ITerrainModifier ----
    public Behaviour Owner => this;
    public int LayerOrder => layerOrder;
    public string Label => gameObject.name;
    public ModifierOperation Operation => operation;

    private void OnEnable()
    {
        transform.hasChanged = false;
        TerrainModifierManager.Register(this);
    }

    private void OnDisable() => TerrainModifierManager.Unregister(this);

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
        radius = Mathf.Max(0.5f, radius);
        TerrainModifierManager.RequestUpdate();
    }

    public void Apply(TerrainHeightContext c)
    {
        Vector3 p = transform.position;
        Vector2 center = new Vector2(p.x, p.z);

        if (!c.TryGetRegion(center - Vector2.one * radius, center + Vector2.one * radius,
                            out int x0, out int z0, out int w, out int h))
            return;

        float invY = 1f / c.size.y;
        float nodeNorm = Mathf.Max(0f, (p.y - c.origin.y) * invY);

        // Offset : écart entre le nœud et le sol sous son centre.
        float centerNorm = c.SampleNormalized(p.x, p.z);
        float offsetNorm = operation == ModifierOperation.Offset ? (p.y - c.origin.y) * invY - centerNorm : nodeNorm;

        float[,] delta = new float[h, w];
        float invR = 1f / radius;

        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2 world = c.PixelToWorld(x0 + x, z0 + z);
                float dist = Vector2.Distance(world, center);
                if (dist > radius) continue;

                float falloff = Mathf.Clamp01(falloffCurve.Evaluate(dist * invR));
                float amount = strength * falloff;
                if (amount <= 0f) continue;

                float current = c.heights[z0 + z, x0 + x];
                delta[z, x] = ModifierMath.Delta(operation, current, nodeNorm, offsetNorm, 1f, amount);
            }
        }

        c.ApplyDelta(delta, x0, z0, smoothness);
    }

    private void OnDrawGizmos()
    {
        if (!showGizmos) return;

        Color col = ModifierMath.ColorOf(operation);
        Vector3 p = transform.position;
        bool selected = UnityEditorSelected();

        // Cercles épousant le terrain : bord + anneaux de falloff.
        DrawGroundRing(p, radius, new Color(col.r, col.g, col.b, selected ? 1f : 0.55f));
        for (int i = 1; i <= 2; i++)
        {
            float t = i / 3f;
            float a = Mathf.Clamp01(falloffCurve.Evaluate(t)) * 0.5f + 0.05f;
            DrawGroundRing(p, radius * t, new Color(col.r, col.g, col.b, a));
        }

        float gy = ModifierMath.GroundY(p.x, p.z, p.y);
        Gizmos.color = new Color(col.r, col.g, col.b, 0.9f);
        Gizmos.DrawLine(p, new Vector3(p.x, gy, p.z));
        Gizmos.DrawSphere(p, Mathf.Max(0.3f, radius * 0.02f));
    }

    private bool UnityEditorSelected()
    {
#if UNITY_EDITOR
        return UnityEditor.Selection.Contains(gameObject);
#else
        return false;
#endif
    }

    private static void DrawGroundRing(Vector3 center, float r, Color color)
    {
        Gizmos.color = color;
        const int seg = 48;
        Vector3 prev = default;
        for (int i = 0; i <= seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            float x = center.x + Mathf.Cos(a) * r;
            float z = center.z + Mathf.Sin(a) * r;
            Vector3 pt = new Vector3(x, ModifierMath.GroundY(x, z, center.y) + 0.15f, z);
            if (i > 0) Gizmos.DrawLine(prev, pt);
            prev = pt;
        }
    }

    // ---- Préréglages de courbe (utilisés par l'éditeur) ----
    public static AnimationCurve CurveLinear() => AnimationCurve.Linear(0f, 1f, 1f, 0f);
    public static AnimationCurve CurveSmooth() => AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
    public static AnimationCurve CurveSoft() =>
        new AnimationCurve(new Keyframe(0f, 1f, 0f, -0.2f), new Keyframe(1f, 0f, -2.5f, 0f));
    public static AnimationCurve CurvePlateau() =>
        new AnimationCurve(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(0.6f, 1f, 0f, -1.2f), new Keyframe(1f, 0f, -0.2f, 0f));
    public static AnimationCurve CurveSpike() =>
        new AnimationCurve(new Keyframe(0f, 1f, 0f, -3f), new Keyframe(1f, 0f, 0f, 0f));
}
