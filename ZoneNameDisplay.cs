using UnityEngine;

namespace RoadTools
{
    public enum ZoneNameAnchor { BottomRight, BottomLeft, TopRight, TopLeft }

    /// <summary>
    /// Affiche le nom de la zone (MapZone) où se trouve la cible : fade in, maintien, fade out, façon GTA V.
    /// IMGUI, aucune dépendance UI. À placer sur n'importe quel objet de la scène.
    /// </summary>
    [AddComponentMenu("Roads/Zone Name Display")]
    public class ZoneNameDisplay : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Le joueur / véhicule. Vide : la caméra principale.")]
        public Transform target;
        [Min(0.05f), Tooltip("Fréquence de détection de la zone (s).")]
        public float checkInterval = 0.2f;
        [Tooltip("Affiche aussi le nom de la zone de départ au lancement du jeu.")]
        public bool showOnStart = true;
        [Tooltip("Nom affiché quand on sort de toutes les zones (vide = rien n'est affiché).")]
        public string fallbackName = "";

        [Header("Timing (secondes)")]
        [Min(0f)] public float fadeIn = 0.6f;
        [Min(0f)] public float hold = 3f;
        [Min(0f)] public float fadeOut = 1.2f;
        [Min(0f), Tooltip("Le texte glisse de cette distance (pixels, base 1080p) pendant le fade in.")]
        public float slideDistance = 18f;

        [Header("Style")]
        public ZoneNameAnchor anchor = ZoneNameAnchor.BottomRight;
        public Vector2 margin = new Vector2(60f, 60f);
        [Min(8f), Tooltip("Taille de police en pixels pour un écran de 1080 px de haut (elle s'adapte à la résolution).")]
        public float fontSize = 44f;
        public Font font;
        public FontStyle fontStyle = FontStyle.Bold;
        public bool uppercase = false;
        public Color textColor = Color.white;
        public Color shadowColor = new Color(0f, 0f, 0f, 0.75f);
        [Min(0f)] public float shadowOffset = 2f;

        private MapZone current;
        private string lastName;
        private bool initialized;
        private float scanTimer;

        private string shown;
        private float t = float.MaxValue;   // temps écoulé depuis le début de l'animation
        private GUIStyle style;

        /// <summary>Lance l'animation avec un texte quelconque (utile pour un événement scripté).</summary>
        public void Show(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            shown = text;
            t = 0f;
        }

        private float Total => fadeIn + hold + fadeOut;

        private void Update()
        {
            if (t < Total) t += Time.unscaledDeltaTime;

            scanTimer -= Time.unscaledDeltaTime;
            if (scanTimer > 0f) return;
            scanTimer = checkInterval;

            Transform tr = target != null ? target : (Camera.main != null ? Camera.main.transform : null);
            if (tr == null) return;

            MapZone z = MapZone.Find(tr.position);
            string name = z != null ? z.displayName : fallbackName;

            if (!initialized)
            {
                initialized = true;
                current = z;
                lastName = name;
                if (showOnStart) Show(name);
                return;
            }

            if (z == current) return;
            current = z;

            // Deux zones voisines avec le même nom : pas de réaffichage.
            if (name == lastName) return;
            lastName = name;
            Show(name);   // zone sans nom ni fallback : on laisse simplement le texte en cours se terminer
        }

        private float Alpha()
        {
            if (t >= Total) return 0f;
            if (t < fadeIn) return fadeIn > 0f ? Mathf.SmoothStep(0f, 1f, t / fadeIn) : 1f;
            if (t < fadeIn + hold) return 1f;
            return fadeOut > 0f ? 1f - Mathf.SmoothStep(0f, 1f, (t - fadeIn - hold) / fadeOut) : 0f;
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            float a = Alpha();
            if (a <= 0.003f || string.IsNullOrEmpty(shown)) return;

            float sc = Screen.height / 1080f;
            if (style == null) style = new GUIStyle(GUI.skin.label) { wordWrap = false, clipping = TextClipping.Overflow };
            style.font = font;
            style.fontStyle = fontStyle;
            style.fontSize = Mathf.Max(8, Mathf.RoundToInt(fontSize * sc));

            bool right = anchor == ZoneNameAnchor.BottomRight || anchor == ZoneNameAnchor.TopRight;
            bool bottom = anchor == ZoneNameAnchor.BottomRight || anchor == ZoneNameAnchor.BottomLeft;
            style.alignment = right ? (bottom ? TextAnchor.LowerRight : TextAnchor.UpperRight)
                                    : (bottom ? TextAnchor.LowerLeft : TextAnchor.UpperLeft);

            string text = uppercase ? shown.ToUpperInvariant() : shown;
            float w = Screen.width * 0.6f, h = style.fontSize * 2f;
            float mx = margin.x * sc, my = margin.y * sc;

            // Glissement léger pendant le fade in uniquement (pas pendant le fade out).
            float slide = t < fadeIn && fadeIn > 0f ? (1f - Mathf.SmoothStep(0f, 1f, t / fadeIn)) * slideDistance * sc : 0f;

            float x = right ? Screen.width - mx - w : mx;
            float y = bottom ? Screen.height - my - h + slide : my - slide;
            Rect r = new Rect(x, y, w, h);

            Color old = GUI.color;
            float o = shadowOffset * sc;
            if (o > 0f)
            {
                Color sh = shadowColor; sh.a *= a;
                GUI.color = Color.white;
                style.normal.textColor = sh;
                GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, style);
            }
            Color c = textColor; c.a *= a;
            style.normal.textColor = c;
            GUI.Label(r, text, style);
            GUI.color = old;
        }
    }
}