using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Shared bottom-right resize grip for overlay panels. Width always resizes (content reflows);
    /// height resizes only for panels that scroll their content (heightEnabled). Drag the grip to resize,
    /// double-click it to reset. Uses InputCompat's polled mouse + per-slot drag arbitration so it never
    /// fights panel-dragging or another overlapping panel.</summary>
    public sealed class PanelResize
    {
        public const float GripSize = 16f;

        private Rect _grip;
        private bool _active;
        private bool _hover;
        private Vector2 _start;
        private float _startW, _startH;
        private float _lastPress = -10f;

        public enum Result { None, Resizing, Committed, Reset }

        public bool Active => _active;

        /// <summary>Draw the grip in the panel's bottom-right corner (call near the end of OnGUI, in the
        /// same local space as the panel rect). Three diagonal ticks; brightens while dragging.</summary>
        public void DrawGrip(Texture2D white, Rect panel)
        {
            _grip = new Rect(panel.xMax - GripSize, panel.yMax - GripSize, GripSize, GripSize);
            bool hot = _active || _hover;
            var prev = GUI.color;
            // soft blue highlight behind the ticks when hovered/dragging, so it reads as grabbable
            if (hot)
            {
                GUI.color = new Color(0.45f, 0.7f, 1f, _active ? 0.35f : 0.20f);
                GUI.DrawTexture(_grip, white);
            }
            GUI.color = new Color(1f, 1f, 1f, hot ? 0.95f : 0.45f);
            for (int i = 1; i <= 3; i++)
            {
                float o = i * 4f;
                GUI.DrawTexture(new Rect(_grip.xMax - o, _grip.yMax - 3f, 3f, 2f), white);
                GUI.DrawTexture(new Rect(_grip.xMax - 3f, _grip.yMax - o, 2f, 3f), white);
            }
            GUI.color = prev;
        }

        /// <summary>Hit-test + drive the resize from HandlePointer (pass the local mouse pos). On Committed
        /// the caller should persist width/height; on Reset it should restore defaults. Any non-None
        /// result means the grip consumed the interaction — the caller should early-out.</summary>
        public Result Handle(int slot, Vector2 m, ref float width, ref float height,
            float minW, float maxW, float minH, float maxH, bool heightEnabled)
        {
            _hover = _grip.Contains(m);   // drives the hover highlight in DrawGrip (next OnGUI)
            if (InputCompat.MousePressed() && _grip.Contains(m))
            {
                float now = Time.realtimeSinceStartup;
                bool dbl = (now - _lastPress) < 0.35f;
                _lastPress = now;
                if (dbl) { _active = false; InputCompat.ReleaseDrag(slot); return Result.Reset; }
                if (!InputCompat.ClaimDrag(slot)) return Result.None;   // a panel on top grabbed it
                _active = true; _start = m; _startW = width; _startH = height;
                return Result.Resizing;
            }
            if (_active)
            {
                if (!InputCompat.OwnsDrag(slot)) { _active = false; return Result.None; }
                if (InputCompat.MouseHeld())
                {
                    width = Mathf.Clamp(_startW + (m.x - _start.x), minW, maxW);
                    if (heightEnabled) height = Mathf.Clamp(_startH + (m.y - _start.y), minH, maxH);
                }
                // Level-triggered release, not just the edge: if the mouse-up is ever missed (focus lost
                // mid-drag, release outside the window) the edge never fires and _active latches — and since
                // any non-None result makes callers early-out, that silently swallows EVERY later click on
                // the panel, including its close button. Treating "button simply isn't down any more" as a
                // release makes it self-heal; it can't misfire because a real drag holds the button down.
                if (InputCompat.MouseReleased() || !InputCompat.MouseHeld())
                { _active = false; InputCompat.ReleaseDrag(slot); return Result.Committed; }
                return Result.Resizing;
            }
            return Result.None;
        }
    }
}
