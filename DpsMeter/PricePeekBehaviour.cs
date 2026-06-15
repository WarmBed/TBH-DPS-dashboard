using System;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay: Steam Community Market price of the item under the native in-game tooltip.
    /// The hovered item is read by HeroProbe.PollHoveredItem; the price/median/volume/7-day history come
    /// from PriceStore (cron-built prices.json on jsDelivr). Shows price, 24h change (波動), median,
    /// listings, 24h volume, and a mini 7-day price curve. Right-click an item to PIN the box to it (it
    /// stays put while you move the mouse onto the curve); hovering a curve point then shows that point's
    /// time, price, and change vs now. The adjust key (default F4) pins it visible and draggable to set
    /// its position. Toggled on/off from the F1 control center.</summary>
    public class PricePeekBehaviour : MonoBehaviour
    {
        public PricePeekBehaviour(IntPtr ptr) : base(ptr) { }

        private const int Slot = 8;        // InputCompat panel slot (input capture in adjust/pinned modes)
        private const float Pad = 8f;
        private const float Width = 240f;
        private const float ChartH = 40f;
        private Rect _rect = new Rect(0, 0, Width, 0);
        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _tinyR, _box;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)
        private float _scale = 1f;
        private bool _adjust;              // position-adjust (drag) mode
        private bool _dragging; private Vector2 _dragOffset;
        private int _pinnedKey;            // 0 = not pinned; otherwise the box is locked to this item
        private string _pinnedLocalized, _pinnedSteamName;

        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);
        private bool Enabled => Plugin.PricePeekEnabled == null || Plugin.PricePeekEnabled.Value;

        void Update()
        {
            try
            {
                if (!Enabled) { if (_pinnedKey != 0) _pinnedKey = 0; return; }
                PriceStore.EnsureLoaded();
                InputCompat.Poll();   // idempotent per frame; make sure right-click edge is fresh

                if (InputCompat.KeyPressed(Plugin.PriceAdjustKey)) _adjust = !_adjust;

                // right-click an item to pin/unpin (the game doesn't use right-click)
                if (InputCompat.RightPressed())
                {
                    int hk = HeroProbe.HoveredKey;
                    if (hk != 0) { if (_pinnedKey == hk) _pinnedKey = 0; else Pin(hk); }
                    else if (_pinnedKey != 0) _pinnedKey = 0;
                }

                bool capture = _adjust || _pinnedKey != 0;
                InputCompat.SetPanel(Slot, capture, ScaledRect());
                if (_adjust) HandleDrag();
                else if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); }
            }
            catch { }
        }

        private void Pin(int k)
        {
            _pinnedKey = k;
            string en = ItemNameStore.GetEn(k), loc = ItemNameStore.Get(k);
            if (string.IsNullOrEmpty(loc)) loc = en;
            if (string.IsNullOrEmpty(loc)) loc = "#" + k;
            _pinnedLocalized = loc;
            _pinnedSteamName = (HeroProbe.HoveredIsGear && !string.IsNullOrEmpty(HeroProbe.HoveredGrade)) ? $"{en} ({HeroProbe.HoveredGrade}) A" : en;
        }

        private void HandleDrag()
        {
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // Slot is the highest panel id, so when panels overlap the drag arbitration gives the press to
            // us (we're drawn on top) and the panel underneath yields — no more dragging two windows at once.
            if (InputCompat.MousePressed() && _rect.Contains(m) && InputCompat.ClaimDrag(Slot))
            { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(Slot)) { _dragging = false; return; }   // a panel on top stole the press
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased())
                { _dragging = false; Plugin.PricePosX.Value = _rect.x; Plugin.PricePosY.Value = _rect.y; InputCompat.ReleaseDrag(Slot); }
            }
        }

        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null) { _bgTex = new Texture2D(1, 1); _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.92f)); _bgTex.Apply(); if (_box != null) _box.normal.background = _bgTex; }
            int fs = Plugin.FontSize.Value, fsm = Plugin.FontSizeSmall.Value;
            if (_stylesReady && _builtFs == fs && _builtFsm == fsm) return;
            _builtFs = fs; _builtFsm = fsm;
            _title = new GUIStyle { fontSize = fs, fontStyle = FontStyle.Bold, richText = true }; _title.normal.textColor = new Color(1f, 0.86f, 0.35f);
            _label = new GUIStyle { fontSize = fs, richText = true }; _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true }; _dim.normal.textColor = new Color(0.78f, 0.84f, 0.95f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true }; _tiny.normal.textColor = new Color(0.62f, 0.68f, 0.78f);
            _tinyR = new GUIStyle(_tiny) { alignment = TextAnchor.UpperRight };   // right-aligned qty in the order book
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _tinyR);
            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!Enabled) return;
            bool pinned = _pinnedKey != 0;
            int key = pinned ? _pinnedKey : HeroProbe.HoveredKey;
            if (key == 0 && !_adjust) return;   // hover-only unless pinned or in adjust mode
            GUI.depth = -20;
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                int fs = Plugin.FontSize.Value; float lh = fs + 6;

                string localized, steamName; PriceStore.Info info = null;
                if (pinned) { localized = _pinnedLocalized; steamName = _pinnedSteamName; info = PriceStore.Get(steamName); }
                else if (key != 0)
                {
                    localized = ItemNameStore.Get(key);
                    string en = ItemNameStore.GetEn(key);
                    if (string.IsNullOrEmpty(localized)) localized = en;
                    if (string.IsNullOrEmpty(localized)) localized = "#" + key;
                    steamName = (HeroProbe.HoveredIsGear && !string.IsNullOrEmpty(HeroProbe.HoveredGrade)) ? $"{en} ({HeroProbe.HoveredGrade}) A" : en;
                    info = PriceStore.Get(steamName);
                }
                else { localized = Loc.G("price_panel"); steamName = ""; }   // adjust-mode placeholder

                bool haveQuote = info != null;
                int[] hist = info?.HistC;
                bool hasChart = hist != null && hist.Length >= 2;
                // pinned -> the full single-item card: fetch this item's order book (Steam's is login-gated,
                // so it comes via the pipeline's detail/<slug>.json). Hover stays compact (no order book).
                DetailStore.OB ob = null;
                if (pinned && haveQuote) { DetailStore.Request(steamName); ob = DetailStore.Get(steamName); }
                bool hasOB = ob != null && (ob.Sell.Length > 0 || ob.Buy.Length > 0);
                int fsm2 = Plugin.FontSizeSmall.Value; float obRowH = fsm2 + 4f;
                int obRowCount = hasOB ? (1 + Math.Min(5, ob.Sell.Length) + 1 + Math.Min(5, ob.Buy.Length)) : 0;
                float obH = hasOB ? 4f + obRowCount * obRowH : 0f;
                float h = Pad + lh * 3f + (hasChart ? ChartH + 4f : 0f) + obH + Pad;
                _scale = UiScale.User;
                _rect.width = Width; _rect.height = h;

                if (!_dragging)
                {
                    float ox, oy;
                    if (Plugin.PricePosX != null && Plugin.PricePosX.Value >= 0f && Plugin.PricePosY.Value >= 0f)
                    { ox = Plugin.PricePosX.Value; oy = Plugin.PricePosY.Value; }
                    else { ox = Screen.width * 0.285f; oy = Screen.height * 0.735f; }   // auto: under the tooltip
                    ox = Mathf.Clamp(ox, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    oy = Mathf.Clamp(oy, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                    _rect.x = ox; _rect.y = oy;
                }

                float ix = _rect.x + Pad, iw = _rect.width - Pad * 2;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);
                if (_adjust) DrawBorder(_rect, new Color(1f, 0.86f, 0.35f, 0.9f));
                else if (pinned) DrawBorder(_rect, new Color(0.4f, 0.85f, 0.95f, 0.9f));

                float cy = _rect.y + Pad;
                GUI.Label(new Rect(ix, cy, iw, lh), (pinned ? "📌 " : "") + localized, _title); cy += lh;

                if (_adjust && key == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>{Loc.G("price_drag_hint")}</color>", _dim); cy += lh;
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9aa3b0>{Plugin.PriceAdjustKey} {Loc.G("price_drag_done")}</color>", _tiny);
                }
                else if (PriceStore.State == PriceStore.St.Loading)
                    GUI.Label(new Rect(ix, cy, iw, lh), "<color=#9fb4cc>Steam 報價載入中…</color>", _dim);
                else if (PriceStore.State == PriceStore.St.Error)
                    GUI.Label(new Rect(ix, cy, iw, lh), "<color=#ef6a5a>報價讀取失敗</color>", _dim);
                else if (haveQuote)
                {
                    string trend = "";
                    if (info.PrevCents > 0)
                    {
                        double pct = (info.Cents - info.PrevCents) * 100.0 / info.PrevCents;
                        string col = pct > 0.05 ? "#5fd07c" : pct < -0.05 ? "#ef6a5a" : "#9aa3b0";
                        string arrow = pct > 0.05 ? "▲" : pct < -0.05 ? "▼" : "→";
                        trend = $"   <color={col}>{arrow}{Math.Abs(pct):0.#}%</color>";
                    }
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#5fd07c>Steam {PriceStore.Format(info.Cents)}</color>{trend}", _label); cy += lh;
                    var sb = new System.Text.StringBuilder();
                    if (info.MedianCents >= 0) sb.Append($"中位 {PriceStore.Format(info.MedianCents)}   ");
                    sb.Append($"在售 {info.Qty}");
                    if (info.Vol >= 0) sb.Append($"   24h成交 {info.Vol}");
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9aa3b0>{sb}</color>", _tiny); cy += lh;
                    if (hasChart) { DrawSparkline(new Rect(ix, cy + 2f, iw, ChartH), hist, info.HistT, info.Cents, pinned); cy += ChartH + 4f; }
                    if (hasOB) DrawOrderBook(ix, cy, iw, ob, obRowH);
                }
                else
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), "<color=#8a93a0>查無 Steam 報價</color>", _dim); cy += lh;
                    if (!string.IsNullOrEmpty(steamName)) GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#5a626e>{steamName}</color>", _tiny);
                }
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        // mini 7-day price curve. When interactive (pinned), hovering a point shows its time / price /
        // change-vs-now. `times` are unix seconds aligned to `cents` (may be null on the old feed format).
        private void DrawSparkline(Rect area, int[] cents, int[] times, int curCents, bool interactive)
        {
            int n = cents.Length;
            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < n; i++) { if (cents[i] < min) min = cents[i]; if (cents[i] > max) max = cents[i]; }
            float span = Mathf.Max(1, max - min);
            float plotW = area.width - 44f;   // room for the price labels on the right
            float x0 = area.x, y0 = area.y, ph = area.height;
            DrawFill(x0, y0, plotW, ph, new Color(1f, 1f, 1f, 0.04f));
            DrawFill(x0, y0, plotW, 1, new Color(1, 1, 1, 0.10f));
            DrawFill(x0, y0 + ph - 1, plotW, 1, new Color(1, 1, 1, 0.10f));
            float dx = n > 1 ? plotW / (n - 1) : 0f;
            var line = new Color(0.45f, 0.7f, 1f, 0.95f);
            Vector2 prev = Vector2.zero;
            for (int i = 0; i < n; i++)
            {
                float t = (cents[i] - min) / span;
                float px = x0 + dx * i;
                float py = y0 + ph - 3f - t * (ph - 6f);
                if (i > 0) DrawSeg(prev, new Vector2(px, py), line);
                prev = new Vector2(px, py);
            }
            DrawFill(prev.x - 2f, prev.y - 2f, 4f, 4f, new Color(0.6f, 0.85f, 1f, 1f));   // "now" dot
            GUI.Label(new Rect(x0 + plotW + 2f, y0 - 3f, 44f, 14f), $"<size=9><color=#9aa3b0>{PriceStore.Format(max)}</color></size>", _tiny);
            GUI.Label(new Rect(x0 + plotW + 2f, y0 + ph - 12f, 44f, 14f), $"<size=9><color=#9aa3b0>{PriceStore.Format(min)}</color></size>", _tiny);
            GUI.Label(new Rect(x0 + 2f, y0 + ph - 11f, 60f, 12f), "<size=9><color=#5a626e>7d</color></size>", _tiny);

            if (!interactive || n < 2) return;
            // hover a point -> marker + detail (time / price / change vs now)
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            if (m.x < x0 - 4f || m.x > x0 + plotW + 4f || m.y < y0 - 4f || m.y > y0 + ph + 4f) return;
            int idx = Mathf.Clamp(Mathf.RoundToInt((m.x - x0) / Mathf.Max(1e-3f, dx)), 0, n - 1);
            float mxp = x0 + dx * idx;
            float myp = y0 + ph - 3f - ((cents[idx] - min) / span) * (ph - 6f);
            DrawFill(mxp - 0.5f, y0, 1f, ph, new Color(1f, 1f, 1f, 0.25f));   // vertical marker
            DrawFill(mxp - 3f, myp - 3f, 6f, 6f, new Color(1f, 0.95f, 0.5f, 1f));
            string when = (times != null && idx < times.Length)
                ? DateTimeOffset.FromUnixTimeSeconds(times[idx]).LocalDateTime.ToString("MM/dd HH:mm") : $"#{idx + 1}";
            double dpct = curCents > 0 && cents[idx] > 0 ? (curCents - cents[idx]) * 100.0 / cents[idx] : 0;
            string dcol = dpct > 0.05 ? "#5fd07c" : dpct < -0.05 ? "#ef6a5a" : "#9aa3b0";
            string sign = dpct > 0 ? "+" : "";
            // detail box near the cursor, kept inside the panel
            float bw = 120f, bh = 44f;
            float bx = Mathf.Clamp(mxp + 8f, _rect.x + 2f, _rect.x + _rect.width - bw - 2f);
            float by = Mathf.Clamp(myp - bh - 6f, _rect.y + 2f, _rect.y + _rect.height - bh - 2f);
            DrawFill(bx, by, bw, bh, new Color(0f, 0f, 0f, 0.95f));
            DrawFill(bx, by, bw, 1, new Color(1, 1, 1, 0.2f));
            GUI.Label(new Rect(bx + 5f, by + 1f, bw - 8f, 14f), $"<size=10><color=#cfd6e0>{when}</color></size>", _tiny);
            GUI.Label(new Rect(bx + 5f, by + 14f, bw - 8f, 14f), $"<size=11><color=#eaf3ee>{PriceStore.Format(cents[idx])}</color></size>", _tiny);
            GUI.Label(new Rect(bx + 5f, by + 28f, bw - 8f, 14f), $"<size=10><color={dcol}>vs現在 {sign}{dpct:0.#}%</color></size>", _tiny);
        }

        // 5-level order book: asks (red, highest at top) / spread / bids (green, highest at top), each row a
        // depth bar proportional to its quantity. Data is from DetailStore (tbh-market via the pipeline).
        private void DrawOrderBook(float x, float y, float w, DetailStore.OB ob, float rh)
        {
            float cy = y;
            GUI.Label(new Rect(x, cy, w, rh), $"<color=#7a8694>{Loc.G("order_book")}</color>", _tiny); cy += rh;
            int ns = Mathf.Min(5, ob.Sell.Length);
            for (int i = ns - 1; i >= 0; i--) { ObRow(x, cy, w, rh, ob.Sell[i], ob.MaxQty, true); cy += rh; }
            GUI.Label(new Rect(x, cy, w, rh), $"<color=#cfd6e0>{PriceStore.Format(ob.LowSell)}  /  {PriceStore.Format(ob.HighBuy)}</color>", _tiny); cy += rh;
            int nb = Mathf.Min(5, ob.Buy.Length);
            for (int i = 0; i < nb; i++) { ObRow(x, cy, w, rh, ob.Buy[i], ob.MaxQty, false); cy += rh; }
        }

        private void ObRow(float x, float y, float w, float rh, DetailStore.Level lv, int maxQty, bool ask)
        {
            float barW = w * Mathf.Clamp01(lv.Qty / (float)Mathf.Max(1, maxQty));
            DrawFill(x + w - barW, y, barW, rh - 1f, ask ? new Color(0.94f, 0.38f, 0.43f, 0.15f) : new Color(0.15f, 0.82f, 0.49f, 0.15f));
            GUI.Label(new Rect(x + 2f, y, w - 4f, rh), $"<color={(ask ? "#f0616d" : "#26d07c")}>{PriceStore.Format(lv.Price)}</color>", _tiny);
            GUI.Label(new Rect(x, y, w - 3f, rh), $"<color=#9aa3b0>{lv.Qty}</color>", _tinyR);
        }

        private void DrawFill(float x, float y, float w, float h, Color c)
        { var p = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(x, y, w, h), _white); GUI.color = p; }

        // thin line as a row of 2px dots (matches TrendChart; safe under the active GUI.matrix)
        private void DrawSeg(Vector2 a, Vector2 b, Color c)
        {
            float len = Vector2.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 3f));
            for (int i = 0; i <= steps; i++) { var p = Vector2.Lerp(a, b, i / (float)steps); DrawFill(p.x - 1, p.y - 1, 2, 2, c); }
        }

        private void DrawBorder(Rect r, Color c)
        {
            var p = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), _white);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), _white);
            GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), _white);
            GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), _white);
            GUI.color = p;
        }

        void Awake()
        {
            PanelRegistry.Register("price", 8, "$", () => Loc.G("price_panel"), KeyCode.None,
                () => Plugin.PricePeekEnabled == null || Plugin.PricePeekEnabled.Value,
                v => { if (Plugin.PricePeekEnabled != null) Plugin.PricePeekEnabled.Value = v; });
        }
    }
}
