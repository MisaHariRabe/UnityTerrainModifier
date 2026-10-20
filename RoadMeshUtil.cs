using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadTools
{
    /// <summary>Constructeur de mesh à 2 submeshes : 0 = chaussée, 1 = trottoirs / bordures.</summary>
    public sealed class MeshBuilder
    {
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int> road = new List<int>();
        public readonly List<int> side = new List<int>();

        public int Add(Vector3 p, Vector2 uv)
        {
            verts.Add(p);
            uvs.Add(uv);
            return verts.Count - 1;
        }

        /// <summary>Triangle orienté pour que sa normale regarde du côté de 'expected'.</summary>
        public void Tri(List<int> sub, int a, int b, int c, Vector3 expected)
        {
            Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            if (Vector3.Dot(n, expected) < 0f) { int t = b; b = c; c = t; }
            sub.Add(a); sub.Add(b); sub.Add(c);
        }

        /// <summary>Quad (sommets dans l'ordre de contour) orienté vers 'expected'.</summary>
        public void Quad(List<int> sub, int a, int b, int c, int d, Vector3 expected)
        {
            Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a])
                      + Vector3.Cross(verts[c] - verts[a], verts[d] - verts[a]);
            if (Vector3.Dot(n, expected) < 0f)
            {
                sub.Add(a); sub.Add(c); sub.Add(b);
                sub.Add(a); sub.Add(d); sub.Add(c);
            }
            else
            {
                sub.Add(a); sub.Add(b); sub.Add(c);
                sub.Add(a); sub.Add(c); sub.Add(d);
            }
        }

        /// <summary>Crée le mesh ; les sommets (monde) sont convertis dans le repère local donné.</summary>
        public Mesh ToMesh(string name, Matrix4x4 worldToLocal)
        {
            var local = new Vector3[verts.Count];
            for (int i = 0; i < local.Length; i++) local[i] = worldToLocal.MultiplyPoint3x4(verts[i]);

            var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            if (verts.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(local);
            m.SetUVs(0, uvs);
            m.subMeshCount = 2;
            m.SetTriangles(road, 0);
            m.SetTriangles(side, 1);
            m.RecalculateNormals();
            m.RecalculateBounds();
            if (verts.Count > 0) m.RecalculateTangents();
            return m;
        }
    }

    public static class RoadUtil
    {
        private static Material fallback;

        public static Material Fallback()
        {
            if (fallback == null)
            {
                var rp = GraphicsSettings.currentRenderPipeline;
                if (rp != null && rp.defaultMaterial != null) fallback = rp.defaultMaterial;
                else
                {
                    var sh = Shader.Find("Standard");
                    if (sh != null) fallback = new Material(sh) { hideFlags = HideFlags.DontSave };
                }
            }
            return fallback;
        }

        public static void Destroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying)
            {
                if (o is GameObject go)
                {
                    // Unity interdit SetParent pendant l'(dés)activation du parent (OnDisable) : on renomme à la place,
                    // pour que parent.Find(name) ne retrouve plus l'objet en attente de destruction.
                    go.name = "_destroyed";
                    go.SetActive(false);
                }
                Object.Destroy(o);
            }
            else Object.DestroyImmediate(o);
        }

        public static Transform GetChild(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            if (t == null)
            {
                var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(parent, false);
                t = go.transform;
            }
            return t;
        }

        public static void RemoveChild(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            if (t == null) return;
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            Destroy(t.gameObject);
        }

        public static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) Destroy(t.GetChild(i).gameObject);
        }

        /// <summary>Les objets générés ne sont pas sauvegardés : ils sont reconstruits à l'activation.</summary>
        public static void MarkDontSave(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.DontSave;
        }

        public static void SetMesh(Transform parent, string name, Mesh mesh, Material roadMat, Material sideMat, bool collider)
        {
            Transform t = GetChild(parent, name);
            GameObject go = t.gameObject;

            var mf = go.GetComponent<MeshFilter>();
            if (mf == null) mf = go.AddComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();

            if (mf.sharedMesh != null && mf.sharedMesh != mesh) Destroy(mf.sharedMesh);
            mf.sharedMesh = mesh;
            mr.sharedMaterials = new[] { roadMat != null ? roadMat : Fallback(), sideMat != null ? sideMat : Fallback() };

            var mc = go.GetComponent<MeshCollider>();
            if (collider)
            {
                if (mc == null) mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = null;
                mc.sharedMesh = mesh;
            }
            else if (mc != null) Destroy(mc);

            go.hideFlags = HideFlags.DontSave;
        }

        public static bool TrySampleTerrainHeight(Vector3 world, out float worldY)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                var d = t.terrainData;
                if (d == null) continue;
                Vector3 o = t.transform.position;
                if (world.x < o.x || world.z < o.z || world.x > o.x + d.size.x || world.z > o.z + d.size.z) continue;
                worldY = t.SampleHeight(world) + o.y;
                return true;
            }
            worldY = 0f;
            return false;
        }
    }

    public static class RoadMeshUtil
    {
        /// <summary>
        /// Ruban de trottoir avec bordure : face de bordure (côté chaussée), dessus, paroi extérieure, + capuchons aux extrémités.
        /// 'edge' = bord de la chaussée, 'outward' = direction horizontale unitaire vers l'extérieur.
        /// </summary>
        public static void AddCurbRibbon(MeshBuilder mb, IList<Vector3> edge, IList<Vector3> outward,
                                         IList<float> widths, float height, float uvTile)
        {
            int n = edge.Count;
            if (n < 2) return;

            Vector3 up = Vector3.up;
            Vector3 H = up * height;
            int pc0 = 0, pc1 = 0, pu0 = 0, pu1 = 0, pw0 = 0, pw1 = 0;
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            float dist = 0f;

            for (int i = 0; i < n; i++)
            {
                if (i > 0) dist += Vector3.Distance(edge[i], edge[i - 1]);
                float v = dist / Mathf.Max(0.1f, uvTile);

                Vector3 b0 = edge[i];
                Vector3 t0 = b0 + H;
                Vector3 o = outward[i] * widths[i];
                Vector3 t1 = t0 + o;
                Vector3 b1 = b0 + o;

                int c0 = mb.Add(b0, new Vector2(0f, v));
                int c1 = mb.Add(t0, new Vector2(0.15f, v));
                int u0 = mb.Add(t0, new Vector2(0f, v));
                int u1 = mb.Add(t1, new Vector2(1f, v));
                int w0 = mb.Add(t1, new Vector2(0f, v));
                int w1 = mb.Add(b1, new Vector2(0.15f, v));

                if (i > 0)
                {
                    Vector3 oAvg = outward[i] + outward[i - 1];
                    mb.Quad(mb.side, pc0, c0, c1, pc1, -oAvg); // face de bordure (vers la route)
                    mb.Quad(mb.side, pu0, u0, u1, pu1, up);    // dessus
                    mb.Quad(mb.side, pw0, w0, w1, pw1, oAvg);  // paroi extérieure
                }
                else
                {
                    s0 = mb.Add(b0, new Vector2(0, 0));
                    s1 = mb.Add(t0, new Vector2(0, 1));
                    s2 = mb.Add(t1, new Vector2(1, 1));
                    s3 = mb.Add(b1, new Vector2(1, 0));
                    Vector3 tan0 = (edge[1] - edge[0]).normalized;
                    mb.Quad(mb.side, s0, s1, s2, s3, -tan0);
                }

                if (i == n - 1)
                {
                    int e0 = mb.Add(b0, new Vector2(0, 0));
                    int e1 = mb.Add(t0, new Vector2(0, 1));
                    int e2 = mb.Add(t1, new Vector2(1, 1));
                    int e3 = mb.Add(b1, new Vector2(1, 0));
                    Vector3 tanN = (edge[n - 1] - edge[n - 2]).normalized;
                    mb.Quad(mb.side, e0, e1, e2, e3, tanN);
                }

                pc0 = c0; pc1 = c1; pu0 = u0; pu1 = u1; pw0 = w0; pw1 = w1;
            }
        }

        /// <summary>Trottoir le long d'une polyligne parcourue dans le sens horaire (l'extérieur est à gauche du parcours).</summary>
        public static void AddClockwisePolylineCurb(MeshBuilder mb, IList<Vector3> poly, float width, float height, float uvTile)
        {
            int n = poly.Count;
            if (n < 2) return;

            var edge = new List<Vector3>(n);
            var outward = new List<Vector3>(n);
            var widths = new List<float>(n);

            for (int i = 0; i < n; i++)
            {
                Vector3 prev = poly[Mathf.Max(i - 1, 0)];
                Vector3 next = poly[Mathf.Min(i + 1, n - 1)];
                Vector3 tan = next - prev; tan.y = 0f;
                if (tan.sqrMagnitude < 1e-8f) tan = Vector3.forward;
                tan.Normalize();
                // Contour horaire (vu du dessus) : l'intérieur est à droite, l'extérieur à gauche.
                Vector3 outDir = -Vector3.Cross(Vector3.up, tan);
                edge.Add(poly[i]);
                outward.Add(outDir.normalized);
                widths.Add(width);
            }
            AddCurbRibbon(mb, edge, outward, widths, height, uvTile);
        }
    }
}