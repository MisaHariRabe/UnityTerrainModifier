#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RoadTools
{
    /// <summary>
    /// Garde les portions de route (sections, règles d'objets latéraux, ponts) à la MÊME position en mètres
    /// quand la longueur de la route change (nœud ajouté, supprimé ou déplacé) : leurs pourcentages début / fin
    /// sont recalculés automatiquement.
    ///
    /// Règles :
    ///  - Une portion qui couvre toute la route (0 % → 100 %) reste « toute la route » : elle suit l'extension.
    ///  - Si les pourcentages ont été modifiés par autre chose dans le même instant (ex : l'outil Couper la route,
    ///    qui fait son propre remappage), on ne touche à rien pour ne pas remapper deux fois.
    /// </summary>
    [InitializeOnLoad]
    public static class RoadRangeKeeper
    {
        private sealed class Record
        {
            public float total;
            public float[] snap;
        }

        private const float MinChange = 0.01f;   // m : en dessous, la longueur est considérée inchangée

        private static readonly Dictionary<Road, Record> records = new Dictionary<Road, Record>();
        private static Road[] roads = new Road[0];
        private static bool dirty = true;

        static RoadRangeKeeper()
        {
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += () => dirty = true;
            Undo.undoRedoPerformed += () => dirty = true;
        }

        private static void Tick()
        {
            if (Application.isPlaying) return;

            if (dirty)
            {
                dirty = false;
                roads = Object.FindObjectsByType<Road>(FindObjectsSortMode.None);

                var stale = new List<Road>();
                foreach (var k in records.Keys) if (k == null) stale.Add(k);
                foreach (var k in stale) records.Remove(k);
            }

            foreach (var road in roads)
            {
                if (road == null || !road.isActiveAndEnabled) continue;

                float total = road.TotalLength;
                if (total < 0.5f) { records.Remove(road); continue; }   // moins de 2 nœuds : on repartira d'une base neuve

                var bridge = road.GetComponent<RoadBridge>();
                float[] snap = Snapshot(road, bridge);

                if (!records.TryGetValue(road, out Record rec))
                {
                    records[road] = new Record { total = total, snap = snap };
                    continue;
                }

                if (Mathf.Abs(total - rec.total) > MinChange)
                {
                    if (SameSnapshot(snap, rec.snap))
                    {
                        Remap(road, bridge, rec.total / total);
                        snap = Snapshot(road, bridge);

                        EditorUtility.SetDirty(road);
                        if (bridge != null) EditorUtility.SetDirty(bridge);
                        road.RequestRebuild();
                        if (bridge != null) bridge.RequestRebuild();
                    }
                    rec.total = total;
                }
                rec.snap = snap;
            }
        }

        /// <summary>ratio = ancienne longueur / nouvelle longueur : pct_nouveau = pct_ancien x ratio (même position en mètres).</summary>
        private static void Remap(Road road, RoadBridge bridge, float ratio)
        {
            foreach (var s in road.sections)
                if (s != null) RemapPair(ref s.start, ref s.end, ratio);

            foreach (var r in road.sideObjects)
                if (r != null) RemapPair(ref r.start, ref r.end, ratio);

            if (bridge != null)
                foreach (var b in bridge.spans)
                    if (b != null) RemapPair(ref b.start, ref b.end, ratio);
        }

        private static void RemapPair(ref float start, ref float end, float ratio)
        {
            if (start <= 0.0001f && end >= 0.9999f) return;   // toute la route : reste toute la route
            start = Mathf.Clamp01(start * ratio);
            end = Mathf.Clamp01(end * ratio);
        }

        // ---------- Détection d'un changement des pourcentages par quelqu'un d'autre ----------
        private static float[] Snapshot(Road road, RoadBridge bridge)
        {
            var list = new List<float>();
            foreach (var s in road.sections) if (s != null) { list.Add(s.start); list.Add(s.end); }
            foreach (var r in road.sideObjects) if (r != null) { list.Add(r.start); list.Add(r.end); }
            if (bridge != null)
                foreach (var b in bridge.spans) if (b != null) { list.Add(b.start); list.Add(b.end); }
            return list.ToArray();
        }

        private static bool SameSnapshot(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
#endif