using System;
using UnityEngine;

/// <summary>
/// Opérations disponibles pour tous les modificateurs de terrain.
/// (L'ordre des valeurs existantes est conservé pour la compatibilité de sérialisation.)
/// </summary>
public enum ModifierOperation
{
    Add,        // Ajoute la hauteur du profil
    Subtract,   // Retire la hauteur du profil
    Multiply,   // Multiplie la hauteur existante
    Blend,      // Interpole vers la hauteur cible (aplanit / crée des plateaux)
    Offset,     // Déplace le terrain de la valeur dont le nœud a été "tiré" du sol
    Raise,      // Monte seulement (ne descend jamais sous la cible)
    Lower       // Descend seulement (ne monte jamais au-dessus de la cible)
}

/// <summary>Tout composant capable de modifier la heightmap d'un terrain.</summary>
public interface ITerrainModifier
{
    Behaviour Owner { get; }
    int LayerOrder { get; }
    string Label { get; }
    ModifierOperation Operation { get; }
    void Apply(TerrainHeightContext ctx);
}

/// <summary>
/// Contexte de travail partagé : heightmap de travail + conversions monde/pixel.
/// heights[z, x] — valeurs normalisées 0..1, comme TerrainData.GetHeights.
/// </summary>
public sealed class TerrainHeightContext
{
    public readonly float[,] heights;
    public readonly int resolution;
    public readonly Vector3 origin;
    public readonly Vector3 size;
    public readonly float cellX;
    public readonly float cellZ;

    public TerrainHeightContext(float[,] heights, int resolution, Vector3 origin, Vector3 size)
    {
        this.heights = heights;
        this.resolution = resolution;
        this.origin = origin;
        this.size = size;
        // La heightmap contient N points => N-1 intervalles.
        cellX = size.x / (resolution - 1);
        cellZ = size.z / (resolution - 1);
    }

    public Vector2 PixelToWorld(int px, int pz) =>
        new Vector2(origin.x + px * cellX, origin.z + pz * cellZ);

    /// <summary>Rectangle de pixels (borné à la heightmap) couvrant une boîte monde XZ.</summary>
    public bool TryGetRegion(Vector2 minXZ, Vector2 maxXZ, out int x0, out int z0, out int w, out int h)
    {
        x0 = z0 = w = h = 0;
        int ix0 = Mathf.Max(0, Mathf.FloorToInt((minXZ.x - origin.x) / cellX));
        int ix1 = Mathf.Min(resolution - 1, Mathf.CeilToInt((maxXZ.x - origin.x) / cellX));
        int iz0 = Mathf.Max(0, Mathf.FloorToInt((minXZ.y - origin.z) / cellZ));
        int iz1 = Mathf.Min(resolution - 1, Mathf.CeilToInt((maxXZ.y - origin.z) / cellZ));
        if (ix1 < ix0 || iz1 < iz0) return false;
        x0 = ix0; z0 = iz0; w = ix1 - ix0 + 1; h = iz1 - iz0 + 1;
        return true;
    }

    /// <summary>Hauteur normalisée (bilinéaire) de la heightmap de travail.</summary>
    public float SampleNormalized(float worldX, float worldZ)
    {
        float fx = Mathf.Clamp((worldX - origin.x) / cellX, 0f, resolution - 1);
        float fz = Mathf.Clamp((worldZ - origin.z) / cellZ, 0f, resolution - 1);
        int x = Mathf.Min((int)fx, resolution - 2);
        int z = Mathf.Min((int)fz, resolution - 2);
        float tx = fx - x, tz = fz - z;
        float a = Mathf.Lerp(heights[z, x], heights[z, x + 1], tx);
        float b = Mathf.Lerp(heights[z + 1, x], heights[z + 1, x + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    /// <summary>Lisse (optionnellement) puis ajoute une grille de deltas à la heightmap de travail.</summary>
    public void ApplyDelta(float[,] delta, int x0, int z0, int smoothness)
    {
        if (smoothness > 0) ModifierMath.Smooth(delta, smoothness);

        int h = delta.GetLength(0), w = delta.GetLength(1);
        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                float d = delta[z, x];
                if (d == 0f) continue;
                int gx = x0 + x, gz = z0 + z;
                heights[gz, gx] = Mathf.Clamp01(heights[gz, gx] + d);
            }
        }
    }
}

public static class ModifierMath
{
    /// <summary>
    /// Delta de hauteur (normalisé) pour une opération donnée.
    /// amount = strength * falloff ; offsetNorm = hauteur de profil normalisée ;
    /// targetNorm = hauteur absolue visée.
    /// </summary>
    public static float Delta(ModifierOperation op, float current, float targetNorm,
                              float offsetNorm, float multiplyFactor, float amount)
    {
        switch (op)
        {
            case ModifierOperation.Add: return offsetNorm * amount;
            case ModifierOperation.Subtract: return -offsetNorm * amount;
            case ModifierOperation.Multiply: return current * multiplyFactor * amount;
            case ModifierOperation.Blend: return (targetNorm - current) * amount;
            case ModifierOperation.Offset: return offsetNorm * amount;
            case ModifierOperation.Raise: return Mathf.Max(0f, targetNorm - current) * amount;
            case ModifierOperation.Lower: return Mathf.Min(0f, targetNorm - current) * amount;
            default: return 0f;
        }
    }

    /// <summary>Lissage (flou 3x3 en croix) avec bords clampés.</summary>
    public static void Smooth(float[,] grid, int passes)
    {
        int h = grid.GetLength(0), w = grid.GetLength(1);
        if (h == 0 || w == 0) return;
        var temp = new float[h, w];

        for (int p = 0; p < passes; p++)
        {
            Buffer.BlockCopy(grid, 0, temp, 0, h * w * sizeof(float));
            for (int z = 0; z < h; z++)
            {
                int zm = Mathf.Max(z - 1, 0), zp = Mathf.Min(z + 1, h - 1);
                for (int x = 0; x < w; x++)
                {
                    int xm = Mathf.Max(x - 1, 0), xp = Mathf.Min(x + 1, w - 1);
                    grid[z, x] = (temp[z, x] * 4f + temp[zm, x] + temp[zp, x] + temp[z, xm] + temp[z, xp]) / 8f;
                }
            }
        }
    }

    public static float Smooth01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    public static Color ColorOf(ModifierOperation op) => op switch
    {
        ModifierOperation.Add => new Color(0.2f, 1f, 0.2f),
        ModifierOperation.Subtract => new Color(1f, 0.25f, 0.25f),
        ModifierOperation.Multiply => new Color(1f, 0.92f, 0.01f),
        ModifierOperation.Blend => new Color(0.7f, 0.2f, 0.85f),
        ModifierOperation.Offset => new Color(0.1f, 0.6f, 1f),
        ModifierOperation.Raise => new Color(1f, 0.55f, 0.1f),
        ModifierOperation.Lower => new Color(0.1f, 0.9f, 0.9f),
        _ => Color.white
    };

    public static string Describe(ModifierOperation op) => op switch
    {
        ModifierOperation.Add => "Ajoute la hauteur du nœud (ou du profil) au terrain.",
        ModifierOperation.Subtract => "Retire la hauteur du nœud (ou du profil) au terrain.",
        ModifierOperation.Multiply => "Multiplie la hauteur existante (point : x(1+force) ; spline : +100 % pour 10 m de profil).",
        ModifierOperation.Blend => "Interpole vers la hauteur du nœud : idéal pour aplanir, créer plateaux et routes.",
        ModifierOperation.Offset => "Point : déplace le terrain de la distance dont le nœud est écarté du sol. Spline : identique à Add.",
        ModifierOperation.Raise => "Monte le terrain jusqu'à la hauteur cible, sans jamais le descendre.",
        ModifierOperation.Lower => "Descend le terrain jusqu'à la hauteur cible, sans jamais le monter (creuser).",
        _ => ""
    };

    /// <summary>Hauteur monde du terrain actif (pour les gizmos).</summary>
    public static float GroundY(float x, float z, float fallback)
    {
        if (TerrainModifierManager.TrySampleBaseHeight(x, z, out float y)) return y;
        var t = Terrain.activeTerrain;
        if (t == null) return fallback;
        return t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
    }
}
