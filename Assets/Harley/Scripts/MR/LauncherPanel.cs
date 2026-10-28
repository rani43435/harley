using TMPro;
using UnityEngine;

namespace Harley
{
    /// <summary>
    /// Harley's app launcher: a grid of app tiles floating in the room.
    /// Tap a tile to open that app in its own window (see AppCatalog).
    /// </summary>
    public class LauncherPanel : SpatialPanel
    {
        static LauncherPanel _instance;

        /// <summary>Shows the launcher in front of the player (creates it the first time).</summary>
        public static void Show(Transform head)
        {
            if (_instance == null)
            {
                var go = new GameObject("Harley Apps");
                _instance = go.AddComponent<LauncherPanel>();
                _instance.head = head;
                _instance.EnsureBuilt();
            }
            if (_instance.gameObject.activeSelf && _instance.Shown) { _instance.gameObject.SetActive(false); _instance.Shown = false; return; }
            _instance.ShowInFront(0.7f, 0.12f);
            _instance.Shown = true;
        }

        bool Shown { get; set; }

        const int Columns = 4;
        const float Tile = 96f, Gap = 22f, LabelH = 26f;

        protected override void Awake()
        {
            base.Awake();
            int rows = Mathf.CeilToInt(AppCatalog.All.Length / (float)Columns);
            float w = Columns * Tile + (Columns - 1) * Gap + 2 * 36f;
            float h = 70f + rows * (Tile + LabelH + Gap) + 18f;
            size = new Vector2(w / PixelsPerMeter, h / PixelsPerMeter);
        }

        protected override void Build()
        {
            var c = Content;
            var title = Label(c, "Apps", 26, text, TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold;
            PlaceTL(title.rectTransform, 36, 18, 200, 36);
            PlaceTL(AddButton(c, "X", () => { gameObject.SetActive(false); Shown = false; }, button, 17).rect, c.rect.width - 36 - 40, 18, 40, 36);

            for (int i = 0; i < AppCatalog.All.Length; i++)
            {
                var app = AppCatalog.All[i];
                int col = i % Columns, row = i / Columns;
                float x = 36f + col * (Tile + Gap);
                float y = 70f + row * (Tile + LabelH + Gap);

                var tile = AddButton(c, "", () => Open(app), app.color, 40);
                PlaceTL(tile.rect, x, y, Tile, Tile);
                tile.image.pixelsPerUnitMultiplier = 0.9f;

                // A soft highlight on the top half, so the tiles feel like app icons
                var shine = Box(tile.rect, "Shine", new Color(1f, 1f, 1f, 0.12f));
                Place(shine.rectTransform, new Vector2(0, 0.5f), Vector2.one, new Vector2(6, 0), new Vector2(-6, -6));
                shine.pixelsPerUnitMultiplier = 1.2f;

                tile.label.text = app.letter;
                tile.label.fontSize = 44;
                tile.label.fontStyle = FontStyles.Bold;
                tile.label.transform.SetAsLastSibling();

                var appLabel = Label(c, app.name, 16, text, TextAlignmentOptions.Center);
                PlaceTL(appLabel.rectTransform, x - 8, y + Tile + 4, Tile + 16, LabelH);
            }
        }

        void Open(AppCatalog.App app)
        {
            WebPanel.Create(app, head);
            gameObject.SetActive(false);
            Shown = false;
        }
    }
}
