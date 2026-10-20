using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoadTools
{
    public enum RoadSide { Left, Right, Both }
    public enum RoadEnd { Start, End }

    public enum SideObjectOrientation
    {
        FaceRoad,       // l'avant de l'objet regarde la route
        AwayFromRoad,   // l'avant de l'objet tourne le dos à la route
        AlongRoad,      // l'avant suit le sens de la route
        Random          // rotation Y aléatoire complète
    }

    public enum SideObjectAnchor
    {
        RoadEdge,           // distance mesurée depuis le bord de la chaussée
        SidewalkOuterEdge   // depuis le bord extérieur du trottoir (s'il existe)
    }

    public enum SideObjectHeight
    {
        RoadLevel,      // à la hauteur de la route
        SnapToTerrain   // posé sur le terrain
    }

    /// <summary>Axe local du prefab qui court le long de la route (sert à l'espacement automatique et à l'alignement).</summary>
    public enum SideObjectAxis
    {
        Z,  // l'avant du prefab (Z) suit la route
        X   // le côté du prefab (X) suit la route (ex : clôtures modélisées "en largeur")
    }

    /// <summary>Où une règle de sideobjects s'applique sur une intersection.</summary>
    public enum IntersectionZone
    {
        Corners,    // le long des coins entre deux branches (trottoir / bord)
        Island      // sur l'îlot central (rond-point uniquement)
    }

    /// <summary>Portion de route (0..1 en distance) avec dimensions et trottoirs propres.</summary>
    [Serializable]
    public class RoadSection
    {
        public string name = "Section";
        public bool enabled = true;

        [Range(0f, 1f)] public float start = 0f;
        [Range(0f, 1f)] public float end = 1f;

        [Tooltip("Si décoché, la section ne modifie pas la largeur de la chaussée (utile pour ajouter juste un trottoir).")]
        public bool overrideWidth = true;
        [Min(0f)] public float leftWidth = 4f;
        [Min(0f)] public float rightWidth = 4f;

        public bool sidewalkLeft = false;
        public bool sidewalkRight = false;
        [Min(0.1f)] public float sidewalkLeftWidth = 2.5f;
        [Min(0.1f)] public float sidewalkRightWidth = 2.5f;
        [Min(0.01f)] public float sidewalkHeight = 0.15f;
    }

    [Serializable]
    public class SideObjectEntry
    {
        public GameObject prefab;
        [Min(0f)] public float weight = 1f;
        [Tooltip("Décoché : ce prefab n'est plus posé dans la scène (il reste dans la liste).")]
        public bool enabled = true;
    }

    /// <summary>Règle de placement d'objets le long de la route (lampadaires, arbres, panneaux...).</summary>
    [Serializable]
    public class SideObjectRule
    {
        public string name = "Side Objects";
        public bool enabled = true;

        [Tooltip("Prefabs tirés au hasard (pondérés par leur poids).")]
        public List<SideObjectEntry> prefabs = new List<SideObjectEntry>();

        public RoadSide side = RoadSide.Both;

        [Range(0f, 1f)] public float start = 0f;
        [Range(0f, 1f)] public float end = 1f;

        [Min(0.5f)] public float spacing = 12f;
        [Tooltip("L'espacement devient la longueur du plus grand prefab actif (+ marge) : les objets se touchent bout à bout (clôtures, glissières...).")]
        public bool useBoundsSpacing = false;
        [Tooltip("Marge ajoutée (ou retirée si négative) à la longueur du prefab en espacement automatique.")]
        public float spacingPadding = 0f;
        [Tooltip("Axe local du prefab qui court le long de la route.")]
        public SideObjectAxis lengthAxis = SideObjectAxis.Z;
        [Tooltip("Orientation 'Le long de la route' : suit aussi la pente de la route.")]
        public bool alignToRoadSlope = false;
        [Tooltip("Intersections uniquement : coins ou îlot central.")]
        public IntersectionZone zone = IntersectionZone.Corners;
        [Range(0f, 1f), Tooltip("Variation aléatoire de l'espacement (fraction de l'espacement).")]
        public float spacingJitter = 0.15f;
        [Range(0f, 1f), Tooltip("Probabilité qu'un emplacement soit réellement occupé.")]
        public float probability = 1f;

        public SideObjectAnchor anchor = SideObjectAnchor.SidewalkOuterEdge;
        [Tooltip("Distance latérale depuis l'ancre (positif = vers l'extérieur, négatif = vers la route).")]
        public float offset = 1f;
        [Min(0f), Tooltip("Variation latérale aléatoire ± (mètres).")]
        public float lateralJitter = 0f;
        [Tooltip("Décalage constant le long de la route (mètres).")]
        public float forwardOffset = 0f;
        [Min(0f), Tooltip("Variation aléatoire ± le long de la route (mètres).")]
        public float forwardJitter = 0f;
        [Tooltip("Décalage vertical (mètres).")]
        public float heightOffset = 0f;
        public SideObjectHeight heightMode = SideObjectHeight.RoadLevel;

        public SideObjectOrientation orientation = SideObjectOrientation.FaceRoad;
        [Tooltip("Rotation Y ajoutée (degrés).")]
        public float yawOffset = 0f;
        [Range(0f, 180f), Tooltip("Variation de rotation Y ± (degrés).")]
        public float randomYaw = 0f;

        [Tooltip("Échelle uniforme aléatoire entre X (min) et Y (max).")]
        public Vector2 scaleRange = Vector2.one;

        public int seed = 1;
    }

    /// <summary>Branche d'une intersection.</summary>
    [Serializable]
    public class IntersectionArm
    {
        [Range(0f, 360f), Tooltip("Direction en degrés (0 = avant local, sens horaire vu du dessus).")]
        public float yaw = 0f;
        [Min(1f), Tooltip("Distance du centre au point de raccord avec la route.")]
        public float length = 14f;
        [Min(0.5f)] public float leftWidth = 4f;
        [Min(0.5f)] public float rightWidth = 4f;

        [Tooltip("Trottoir dans le coin qui suit cette branche (sens horaire, vu du dessus).")]
        public bool sidewalkToNext = true;

        [Tooltip("Route raccordée à cette branche (optionnel).")]
        public Road road;
        public RoadEnd roadEnd = RoadEnd.Start;
    }

    public struct RoadPoint
    {
        public Vector3 pos;
        public Vector3 tangent;   // horizontal, normalisé
        public Vector3 right;     // horizontal, normalisé
        public float dist;        // mètres depuis le début
        public float s;           // 0..1
    }

    public struct RoadProfile
    {
        public float left, right;
        public bool swLeft, swRight;
        public float swLeftWidth, swRightWidth, swHeight;
    }
}