#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

namespace RoadTools
{
    /// <summary>
    /// Outil scène pour la Route sélectionnée (pas besoin d'ouvrir le mode spline) :
    ///  - Ctrl+clic (Cmd+clic sur Mac) sur le terrain : ajoute un nœud à la FIN de la route.
    ///  - Poignées sur les deux extrémités (début / fin) : glisse-les pour les déplacer.
    ///  - Shift + glisser une extrémité près d'une branche libre d'une intersection : snap + liaison.
    ///  - Glisser une extrémité déjà connectée SANS Shift : détache la route de l'intersection.
    /// </summary>
    [InitializeOnLoad]
    public static class RoadDrawTool
    {
        private const float SnapPixels = 45f;   // distance écran max pour accrocher une branche

        private static bool hoverValid;
        private static Vector3 hoverPoint;

        static RoadDrawTool() { SceneView.duringSceneGui += OnScene; }

        private static void OnScene(SceneView sv)
        {
            // Le pinceau / lasso de spline a ses propres clics : on lui laisse la main.
            if (SplinePaintTool.Mode != SplinePaintMode.Edit) return;

            GameObject go = Selection.activeGameObject;
            if (go == null) return;
            Road road = go.GetComponent<Road>();
            if (road == null) return;
            SplineContainer container = road.GetContainer();
            if (container == null || container.Spline == null) return;

            var hubs = Object.FindObjectsByType<RoadIntersection>(FindObjectsSortMode.None);
            Spline sp = container.Spline;

            HandleDraw(sv, road, sp, hubs);
            DrawEndpoints(sv, road, container, hubs);
            DrawHint(sv);
        }

        // ------------------------------------------------------------------ Ctrl+clic : poser des nœuds
        private static void HandleDraw(SceneView sv, Road road, Spline sp, RoadIntersection[] hubs)
        {
            Event e = Event.current;
            bool mod = (e.control || e.command) && !e.alt;
            int id = GUIUtility.GetControlID(FocusType.Passive);   // toujours appelé, pour garder des ID stables

            if (!mod) { hoverValid = false; return; }

            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                hoverValid = TerrainRay.TryHit(e.mousePosition, out hoverPoint);
                if (e.type == EventType.MouseMove) sv.Repaint();
            }

            if (e.GetTypeForControl(id) == EventType.MouseDown && e.button == 0 && hoverValid && HandleUtility.nearestControl == id)
            {
                e.Use();
                if (FindLink(hubs, road, RoadEnd.End, out RoadIntersection lh, out _))
                {
                    Debug.LogWarning($"[Road System] La fin de « {road.name} » est connectée à « {lh.name} » : détache-la (glisse le nœud sans Shift) avant de continuer le tracé.");
                    return;
                }
                AddKnot(road, sp, hoverPoint);
                return;
            }

            if (e.type == EventType.Repaint && hoverValid)
            {
                Vector3 up = Vector3.up * 0.25f;
                Handles.color = new Color(1f, 0.9f, 0.2f);
                Handles.DrawWireDisc(hoverPoint + up, Vector3.up, 1.5f);
                if (sp.Count > 0)
                {
                    Vector3 last = road.transform.TransformPoint((Vector3)sp[sp.Count - 1].Position);
                    Handles.DrawDottedLine(last + up, hoverPoint + up, 5f);
                }
            }
        }

        private static void AddKnot(Road road, Spline sp, Vector3 world)
        {
            SplineContainer c = road.GetContainer();
            Undo.RecordObject(c, "Ajouter un nœud de route");
            Vector3 local = road.transform.InverseTransformPoint(world);
            sp.Add(new BezierKnot(new float3(local.x, local.y, local.z)), TangentMode.AutoSmooth);
            sp.Closed = false;
            EditorUtility.SetDirty(c);
            road.RequestRebuild();
        }

        // ------------------------------------------------------------------ Extrémités : déplacer / connecter
        private static void DrawEndpoints(SceneView sv, Road road, SplineContainer container, RoadIntersection[] hubs)
        {
            Spline sp = container.Spline;
            int n = sp.Count;
            if (n < 2) return;

            Event e = Event.current;
            Vector3 lift = Vector3.up * 0.3f;

            foreach (RoadEnd end in new[] { RoadEnd.Start, RoadEnd.End })
            {
                int idx = end == RoadEnd.Start ? 0 : n - 1;
                Vector3 pos = road.transform.TransformPoint((Vector3)sp[idx].Position);
                bool linked = FindLink(hubs, road, end, out RoadIntersection lh, out IntersectionArm la);

                float size = HandleUtility.GetHandleSize(pos) * 0.16f;
                Handles.color = linked ? new Color(0.3f, 1f, 0.4f)
                                       : (end == RoadEnd.Start ? new Color(0.3f, 0.85f, 1f) : new Color(1f, 0.65f, 0.2f));

                EditorGUI.BeginChangeCheck();
                Vector3 np = Handles.FreeMoveHandle(pos + lift, size, Vector3.zero, Handles.SphereHandleCap);
                bool moved = EditorGUI.EndChangeCheck();

                Handles.Label(pos + lift + Vector3.up * size * 2.5f,
                    (end == RoadEnd.Start ? "Début" : "Fin") + (linked ? $"  →  {lh.name}" : "  (Shift + glisser vers une branche)"));

                if (moved)
                {
                    if (e.shift && FindNearestArm(hubs, road, end, e.mousePosition, out RoadIntersection th, out IntersectionArm ta))
                    {
                        Link(road, end, th, ta, hubs);
                    }
                    else
                    {
                        if (linked)
                        {
                            Unlink(lh, la);
                            Undo.RecordObject(container, "Détacher la route");
                            sp.SetTangentMode(idx, TangentMode.AutoSmooth);
                        }
                        Vector3 target = TerrainRay.TryHit(e.mousePosition, out Vector3 hit) ? hit : np - lift;
                        MoveKnot(road, container, idx, target);
                    }
                }
            }

            // Shift tenu : on met en évidence les branches accrochables, la plus proche en vert.
            if (e.shift && e.type == EventType.Repaint)
            {
                FindNearestArm(hubs, road, RoadEnd.End, e.mousePosition, out RoadIntersection nh, out IntersectionArm na);
                foreach (var h in hubs)
                {
                    if (h == null || !h.isActiveAndEnabled) continue;
                    foreach (var a in h.arms)
                    {
                        if (a == null || (a.road != null && a.road != road)) continue;
                        Vector3 p = h.ArmFar(a) + lift;
                        float s = HandleUtility.GetHandleSize(p);
                        bool near = a == na;
                        Handles.color = near ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 1f, 1f, 0.6f);
                        Handles.DrawWireDisc(p, Vector3.up, s * (near ? 0.35f : 0.22f));
                    }
                }
            }
        }

        private static void MoveKnot(Road road, SplineContainer c, int idx, Vector3 world)
        {
            Undo.RecordObject(c, "Déplacer l'extrémité de la route");
            Spline sp = c.Spline;
            BezierKnot k = sp[idx];
            Vector3 local = road.transform.InverseTransformPoint(world);
            k.Position = new float3(local.x, local.y, local.z);
            sp.SetKnot(idx, k);
            EditorUtility.SetDirty(c);
        }

        // ------------------------------------------------------------------ Liaison route <-> intersection
        /// <summary>Branche déjà liée à cette extrémité de la route (s'il y en a une).</summary>
        private static bool FindLink(RoadIntersection[] hubs, Road road, RoadEnd end, out RoadIntersection hub, out IntersectionArm arm)
        {
            foreach (var h in hubs)
            {
                if (h == null) continue;
                foreach (var a in h.arms)
                    if (a != null && a.road == road && a.roadEnd == end) { hub = h; arm = a; return true; }
            }
            hub = null; arm = null;
            return false;
        }

        /// <summary>Branche libre (ou déjà liée à cette extrémité) la plus proche de la souris, à l'écran.</summary>
        private static bool FindNearestArm(RoadIntersection[] hubs, Road road, RoadEnd end, Vector2 mouse,
                                           out RoadIntersection hub, out IntersectionArm arm)
        {
            hub = null; arm = null;
            float best = SnapPixels;
            foreach (var h in hubs)
            {
                if (h == null || !h.isActiveAndEnabled) continue;
                foreach (var a in h.arms)
                {
                    if (a == null) continue;
                    if (a.road != null && !(a.road == road && a.roadEnd == end)) continue;   // déjà prise par une autre route
                    float d = Vector2.Distance(HandleUtility.WorldToGUIPoint(h.ArmFar(a)), mouse);
                    if (d < best) { best = d; hub = h; arm = a; }
                }
            }
            return arm != null;
        }

        private static void Link(Road road, RoadEnd end, RoadIntersection hub, IntersectionArm arm, RoadIntersection[] hubs)
        {
            // Cette extrémité ne peut être liée qu'à une seule branche.
            foreach (var h in hubs)
            {
                if (h == null) continue;
                foreach (var a in h.arms)
                    if (a != null && a != arm && a.road == road && a.roadEnd == end) Unlink(h, a);
            }

            if (arm.road != road || arm.roadEnd != end)
            {
                Undo.RecordObject(hub, "Connecter la route à l'intersection");
                arm.road = road;
                arm.roadEnd = end;
                EditorUtility.SetDirty(hub);
            }

            hub.SnapRoad(arm);          // place le nœud sur la branche, bien aligné
            hub.RequestRebuild(false);  // l'intersection reprend les largeurs de la route
            road.RequestRebuild();
        }

        private static void Unlink(RoadIntersection hub, IntersectionArm arm)
        {
            Undo.RecordObject(hub, "Détacher la route");
            arm.road = null;
            EditorUtility.SetDirty(hub);
            hub.RequestRebuild(false);
        }

        private static void DrawHint(SceneView sv)
        {
            Handles.BeginGUI();
            GUI.Label(new Rect(12, sv.position.height - 62, 640, 20),
                "Route : Ctrl+clic = ajouter un nœud   •   Shift + glisser une extrémité sur une branche = connecter",
                EditorStyles.boldLabel);
            Handles.EndGUI();
        }
    }

    /// <summary>
    /// Fait suivre les routes connectées en temps réel quand on déplace / tourne une intersection dans l'éditeur.
    /// Tourne à chaque tick de l'éditeur (pas seulement quand l'objet se redessine).
    /// </summary>
    [InitializeOnLoad]
    public static class RoadIntersectionFollower
    {
        private struct Pose { public Vector3 pos; public Quaternion rot; }

        private static readonly Dictionary<RoadIntersection, Pose> last = new Dictionary<RoadIntersection, Pose>();
        private static RoadIntersection[] hubs = new RoadIntersection[0];
        private static bool dirty = true;

        static RoadIntersectionFollower()
        {
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += () => dirty = true;
            Undo.undoRedoPerformed += () => dirty = true;
        }

        private static void Tick()
        {
            if (Application.isPlaying) return;   // en Play, RoadIntersection.Update s'en charge déjà

            if (dirty)
            {
                dirty = false;
                hubs = Object.FindObjectsByType<RoadIntersection>(FindObjectsSortMode.None);
                var stale = new List<RoadIntersection>();
                foreach (var k in last.Keys) if (k == null) stale.Add(k);
                foreach (var k in stale) last.Remove(k);
            }

            foreach (var h in hubs)
            {
                if (h == null || !h.isActiveAndEnabled || !h.autoSnapRoads) continue;

                Transform t = h.transform;
                var cur = new Pose { pos = t.position, rot = t.rotation };
                if (!last.TryGetValue(h, out Pose old)) { last[h] = cur; continue; }

                if (old.pos != cur.pos || old.rot != cur.rot)
                {
                    last[h] = cur;
                    h.SnapRoads();   // les nœuds des routes liées suivent tout de suite ; Road se reconstruit via Spline.Changed
                }
            }
        }
    }
}
#endif