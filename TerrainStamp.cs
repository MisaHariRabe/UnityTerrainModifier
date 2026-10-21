using UnityEngine;

/// <summary>
/// Stamp de terrain : une heightmap en MÈTRES (relative, pas normalisée) + sa taille native.
/// Créé depuis un TerrainData (ex : issu d'un outil mesh -> terrain) via la fenêtre "Stamp Creator".
/// Asset en lecture seule à l'exécution : c'est le TerrainStampModifier qui l'applique, non destructivement.
/// </summary>
public class TerrainStamp : ScriptableObject
{
    [Tooltip("Taille (X, Z) en mètres du terrain d'origine. Sert de taille par défaut au placement.")]
    public Vector2 nativeSize = new Vector2(100f, 100f);

    [SerializeField, HideInInspector] private float[] heights;      // [z * resolution + x], en mètres
    [SerializeField, HideInInspector] private int resolution;
    [SerializeField, HideInInspector] private float minHeight;
    [SerializeField, HideInInspector] private float maxHeight;

    public int Resolution => resolution;
    public float MinHeight => minHeight;
    public float MaxHeight => maxHeight;
    public bool IsValid => heights != null && resolution >= 2 && heights.Length == resolution * resolution;

    public void SetData(float[] meters, int res, Vector2 size)
    {
        heights = meters;
        resolution = res;
        nativeSize = size;

        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < meters.Length; i++)
        {
            float v = meters[i];
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        minHeight = mn;
        maxHeight = mx;
    }

    /// <summary>Hauteur en mètres (bilinéaire) ; u, v dans 0..1 (u = X du stamp, v = Z du stamp).</summary>
    public float Sample(float u, float v)
    {
        if (!IsValid) return 0f;
        float fx = Mathf.Clamp01(u) * (resolution - 1);
        float fz = Mathf.Clamp01(v) * (resolution - 1);
        int x = Mathf.Min((int)fx, resolution - 2);
        int z = Mathf.Min((int)fz, resolution - 2);
        float tx = fx - x, tz = fz - z;
        int i0 = z * resolution + x, i1 = i0 + resolution;
        float a = Mathf.Lerp(heights[i0], heights[i0 + 1], tx);
        float b = Mathf.Lerp(heights[i1], heights[i1 + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }
}