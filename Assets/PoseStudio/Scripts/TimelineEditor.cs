using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PoseStudio
{
    // Timeline editor: a SEPARATE window (opened from the main Pose Studio window) that
    // visualises and edits an animation item's keyframe timeline — YouCut-style strip
    // with draggable key diamonds, a scrubbable playhead, transport, zoom/pan, easing
    // per segment, capture/add/delete. It is purely a second VIEW over the existing
    // PoseItem.keyframes data: the classic dropdown + sliders workflow in the main
    // window keeps working and stays in sync both ways.
    public class TimelineEditor
    {
        PoseStudioPlugin plugin;
        GameObject window;
        bool wired;

        // control refs
        Text title, timeText, keyText, statusText;
        Button playBtn; Text playBtnText;
        Toggle snapToggle;
        Slider speedSlider, zoomSlider, panSlider;
        InputField speedInput, secInput;
        Dropdown easeDrop;
        RectTransform view, content;

        // runtime-built strip pieces
        RectTransform playhead, scrubPad, stripBar;
        readonly List<GameObject> dynamic = new List<GameObject>();   // rebuilt per layout
        readonly List<RectTransform> keyHandles = new List<RectTransform>();

        // state
        PoseItem boundItem;
        int builtKeyCount = -1;
        int selIdx = -1;
        float pxPerSec = 60f;
        int dragIdx = -1;
        bool scrubbing;
        bool suppress;
        const float PAD = 12f;          // left/right padding inside the strip content
        const float MIN_SEG = 0.05f;    // minimum segment length (seconds)

        // ----- P2: lanes / capture-paste mask / key clipboard -----
        float padLeft = PAD;            // time-axis origin; widens in lanes mode to clear the gutter
        Toggle lanesToggle;
        Slider laneScrollSlider;
        readonly HashSet<string> expandedGroups = new HashSet<string>();
        readonly HashSet<string> maskOff = new HashSet<string>();   // excluded cats ("bone") or full channel ids
        readonly List<RectTransform> gutterRows = new List<RectTransform>();  // pan-pinned lane labels
        PoseKeyframe clipboard;         // survives item switches — paste works across items
        const float GUT_W = 150f;       // gutter (lane label column) width
        const float LANE_TOP = 26f;     // lanes area starts under the ruler
        const float LANE_H = 17f;
        const float LANE_BOT = 129f;

        static Font UiFont() { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }

        // ---------------------------------------------------------------- setup
        public void Setup(PoseStudioPlugin owner, GameObject prefab)
        {
            plugin = owner;
            if (window != null || prefab == null) return;
            try { window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(prefab); }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] timeline prefab failed: " + e.Message); window = null; }
            if (window == null) return;

            RectTransform wrt = window.GetComponent<RectTransform>();
            if (wrt != null) wrt.anchoredPosition = new Vector2(0f, -40f);

            title = FindIn<Text>("Title");
            timeText = FindIn<Text>("Text_TLTime");
            keyText = FindIn<Text>("Text_TLKey");
            statusText = FindIn<Text>("Text_TLStatus");
            playBtn = FindIn<Button>("Button_TLPlay");
            if (playBtn != null) playBtnText = playBtn.GetComponentInChildren<Text>(true);
            snapToggle = FindIn<Toggle>("Toggle_TLSnap");
            speedSlider = FindIn<Slider>("Slider_tlspeed");
            speedInput = FindIn<InputField>("Input_tlspeed");
            zoomSlider = FindIn<Slider>("Slider_tlzoom");
            panSlider = FindIn<Slider>("Slider_tlpan");
            secInput = FindIn<InputField>("Input_tlsec");
            easeDrop = FindIn<Dropdown>("Dropdown_TLEase");
            view = FindIn<RectTransform>("TLView");
            content = FindIn<RectTransform>("TLContent");

            Button close = FindIn<Button>("Button_TLClose");
            if (close != null) close.onClick.AddListener(Hide);
            if (playBtn != null) playBtn.onClick.AddListener(OnPlayPause);

            Button cap = FindIn<Button>("Button_TLCapture");
            if (cap != null) cap.onClick.AddListener(OnCapture);
            Button addAt = FindIn<Button>("Button_TLAddAt");
            if (addAt != null) addAt.onClick.AddListener(OnAddAtPlayhead);
            Button del = FindIn<Button>("Button_TLDelete");
            if (del != null) del.onClick.AddListener(OnDelete);

            if (speedSlider != null) speedSlider.onValueChanged.AddListener(OnSpeedSlider);
            if (speedInput != null) speedInput.onEndEdit.AddListener(OnSpeedTyped);
            if (zoomSlider != null) zoomSlider.onValueChanged.AddListener(delegate(float v) { if (!suppress) Rebuild(); });
            if (panSlider != null) panSlider.onValueChanged.AddListener(delegate(float v) { if (!suppress) ApplyPan(); });
            if (secInput != null) secInput.onEndEdit.AddListener(OnSecondsTyped);
            if (easeDrop != null)
            {
                easeDrop.ClearOptions();
                easeDrop.AddOptions(new List<string> { "linear", "smooth", "ease in", "ease out" });
                easeDrop.onValueChanged.AddListener(OnEaseChanged);
            }
            lanesToggle = FindIn<Toggle>("Toggle_TLLanes");
            laneScrollSlider = FindIn<Slider>("Slider_tllscroll");
            if (lanesToggle != null) lanesToggle.onValueChanged.AddListener(delegate(bool v) { if (!suppress) Rebuild(); });
            if (laneScrollSlider != null) laneScrollSlider.onValueChanged.AddListener(delegate(float v) { if (!suppress) Rebuild(); });
            Button cpy = FindIn<Button>("Button_TLCopy");
            if (cpy != null) cpy.onClick.AddListener(OnCopy);
            Button psi = FindIn<Button>("Button_TLPasteInto");
            if (psi != null) psi.onClick.AddListener(OnPasteInto);
            Button psn = FindIn<Button>("Button_TLPasteNew");
            if (psn != null) psn.onClick.AddListener(OnPasteNew);
            Button mka = FindIn<Button>("Button_TLMaskAll");
            if (mka != null) mka.onClick.AddListener(OnMaskAll);
            Button mkn = FindIn<Button>("Button_TLMaskNone");
            if (mkn != null) mkn.onClick.AddListener(OnMaskNone);

            wired = true;
            window.SetActive(false);
        }

        T FindIn<T>(string name) where T : Component
        {
            if (window == null) return null;
            Transform[] all = window.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == name)
                {
                    T c = all[i].GetComponent<T>();
                    if (c != null) return c;
                }
            return null;
        }

        // ---------------------------------------------------------------- show/hide
        public void ToggleVisible()
        {
            if (window == null) { plugin.PublicStatus("timeline window unavailable (prefab missing)"); return; }
            if (window.activeSelf) { Hide(); return; }
            PoseItem it = plugin.CurrentItem;
            if (it == null) { plugin.PublicStatus("select or create an item first"); return; }
            if (it.type != "animation" || !it.useKeyframes)
            { plugin.PublicStatus("turn on the keyframe timeline for this animation item first"); return; }
            boundItem = it;
            window.SetActive(true);
            window.transform.SetAsLastSibling();
            selIdx = plugin.CurrentKeyIndex();
            SyncSpeedUI();
            Rebuild();
        }

        public void Hide()
        {
            if (window != null) window.SetActive(false);
            scrubbing = false; dragIdx = -1;
        }

        // ---------------------------------------------------------------- sync from plugin
        public void OnKeysChanged()
        {
            if (window == null || !window.activeSelf) return;
            selIdx = plugin.CurrentKeyIndex();
            Rebuild();
        }

        public void OnPluginKeySelected(int idx)
        {
            if (window == null || !window.activeSelf || suppress) return;
            selIdx = idx;
            RefreshSelection();
        }

        // ---------------------------------------------------------------- helpers
        List<PoseKeyframe> Keys() { return boundItem != null ? boundItem.keyframes : null; }

        float Cycle()
        {
            List<PoseKeyframe> ks = Keys();
            float c = 0f;
            if (ks != null) for (int i = 0; i < ks.Count; i++) if (ks[i] != null) c += Mathf.Max(0.01f, ks[i].seconds);
            return c;
        }

        float KeyTime(int idx)
        {
            List<PoseKeyframe> ks = Keys();
            float t = 0f;
            if (ks != null) for (int i = 0; i < idx && i < ks.Count; i++) if (ks[i] != null) t += Mathf.Max(0.01f, ks[i].seconds);
            return t;
        }

        static RectTransform PlaceRT(GameObject go, Transform parent, float x, float y, float w, float h)
        {
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
            return rt;
        }

        static Text MakeTxt(Transform parent, string txt, float x, float y, float w, float h, int size, TextAnchor anchor, Color col)
        {
            GameObject go = new GameObject("txt", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Text t = go.GetComponent<Text>();
            t.text = txt; t.font = UiFont(); t.fontSize = size; t.alignment = anchor; t.color = col;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            PlaceRT(go, parent, x, y, w, h);
            return t;
        }

        static Image MakeImg(Transform parent, string name, float x, float y, float w, float h, Color col, bool raycast)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            Image im = go.GetComponent<Image>();
            im.color = col; im.raycastTarget = raycast;
            PlaceRT(go, parent, x, y, w, h);
            return im;
        }

        void AddTrigger(GameObject go, EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> act)
        {
            EventTrigger et = go.GetComponent<EventTrigger>();
            if (et == null) et = go.AddComponent<EventTrigger>();
            EventTrigger.Entry en = new EventTrigger.Entry();
            en.eventID = type;
            en.callback.AddListener(act);
            et.triggers.Add(en);
        }

        bool PointerToContentX(PointerEventData e, out float x)
        {
            x = 0f;
            if (content == null) return false;
            Vector2 lp;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, e.position, e.pressEventCamera, out lp)) return false;
            x = lp.x;
            return true;
        }

        float SnapTime(float t)
        {
            if (snapToggle != null && snapToggle.isOn) return Mathf.Round(t * 10f) / 10f;
            return t;
        }

        // ---------------------------------------------------------------- rebuild
        public void Rebuild()
        {
            if (!wired || window == null || !window.activeSelf) return;
            for (int i = 0; i < dynamic.Count; i++) if (dynamic[i] != null) UnityEngine.Object.Destroy(dynamic[i]);
            dynamic.Clear(); keyHandles.Clear(); gutterRows.Clear();
            playhead = null; scrubPad = null; stripBar = null;

            PoseItem it = boundItem;
            List<PoseKeyframe> ks = Keys();
            if (it == null || ks == null || ks.Count == 0 || content == null || view == null)
            { builtKeyCount = -1; return; }
            builtKeyCount = ks.Count;
            if (title != null) title.text = "Timeline — " + it.name;

            bool lanes = lanesToggle != null && lanesToggle.isOn;
            padLeft = lanes ? GUT_W + 10f : PAD;

            float cycle = Cycle();
            float viewW = view.rect.width;
            if (viewW < 50f) viewW = 860f;
            float zoom = zoomSlider != null ? Mathf.Max(1f, zoomSlider.value) : 1f;
            pxPerSec = Mathf.Max(20f, (viewW - padLeft - PAD) / Mathf.Max(0.05f, cycle)) * zoom;
            float contentW = cycle * pxPerSec + padLeft + PAD;
            content.sizeDelta = new Vector2(contentW, content.sizeDelta.y);

            float H = content.sizeDelta.y;            // ~130
            float stripTop = 26f, stripH = 64f;
            float diamondY = lanes ? LANE_TOP + LANE_H * 0.5f : stripTop + stripH * 0.5f;

            // scrub pad: whole-content press/drag surface (behind everything else)
            Image pad = MakeImg(content, "TLScrubPad", 0f, 0f, contentW, H, new Color(1f, 1f, 1f, 0.004f), true);
            scrubPad = pad.rectTransform; dynamic.Add(pad.gameObject);
            AddTrigger(pad.gameObject, EventTriggerType.PointerDown, OnScrub);
            AddTrigger(pad.gameObject, EventTriggerType.Drag, OnScrub);
            AddTrigger(pad.gameObject, EventTriggerType.PointerUp, delegate(BaseEventData d) { scrubbing = false; });

            // strip bar (classic single-strip mode only; lanes mode draws rows instead)
            if (!lanes)
            {
                Image bar = MakeImg(content, "TLStrip", PAD, stripTop, contentW - 2f * PAD, stripH, new Color(0.28f, 0.26f, 0.45f, 1f), false);
                stripBar = bar.rectTransform; dynamic.Add(bar.gameObject);
            }

            // ruler ticks + labels
            int lblStep = cycle > 120f ? 10 : (cycle > 40f ? 5 : 1);
            bool halfTicks = pxPerSec > 46f;
            for (float t = 0f; t <= cycle + 0.001f; t += halfTicks ? 0.5f : 1f)
            {
                bool whole = Mathf.Abs(t - Mathf.Round(t)) < 0.01f;
                float x = padLeft + t * pxPerSec;
                Image tick = MakeImg(content, "tick", x, whole ? 8f : 14f, 1f, whole ? 16f : 10f, new Color(1f, 1f, 1f, whole ? 0.55f : 0.28f), false);
                dynamic.Add(tick.gameObject);
                if (whole && ((int)Mathf.Round(t)) % lblStep == 0)
                {
                    Text lbl = MakeTxt(content, Mathf.Round(t).ToString("0") + "s", x + 3f, 2f, 40f, 14f, 10, TextAnchor.UpperLeft, new Color(1f, 1f, 1f, 0.75f));
                    dynamic.Add(lbl.gameObject);
                }
            }

            // per-group lanes (small change markers behind the master diamonds)
            List<LaneDef> laneDefs = null;
            if (lanes) { laneDefs = ComputeLanes(ks); DrawLaneMarkers(ks, laneDefs); }

            // key diamonds
            for (int i = 0; i < ks.Count; i++)
            {
                int idx = i;   // C#5: capture a copy for the closures below
                float kx = padLeft + KeyTime(i) * pxPerSec;
                GameObject dgo = new GameObject("TLKey" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                Image dim = dgo.GetComponent<Image>();
                dim.color = new Color(0.36f, 0.83f, 0.66f, 1f);
                RectTransform drt = PlaceRT(dgo, content, kx - 8f, diamondY - 8f, 16f, 16f);
                drt.pivot = new Vector2(0.5f, 0.5f);
                drt.anchoredPosition = new Vector2(kx, -diamondY);
                drt.localRotation = Quaternion.Euler(0f, 0f, 45f);
                dynamic.Add(dgo); keyHandles.Add(drt);
                AddTrigger(dgo, EventTriggerType.PointerDown, delegate(BaseEventData d) { OnKeyDown(idx); });
                AddTrigger(dgo, EventTriggerType.Drag, delegate(BaseEventData d) { OnKeyDrag(idx, (PointerEventData)d); });
                AddTrigger(dgo, EventTriggerType.PointerUp, delegate(BaseEventData d) { OnKeyUp(idx); });

                if (!lanes)
                {
                    Text klbl = MakeTxt(content, (i + 1).ToString(), kx - 20f, stripTop + stripH + 6f, 40f, 14f, 10, TextAnchor.UpperCenter, new Color(1f, 1f, 1f, 0.8f));
                    dynamic.Add(klbl.gameObject);
                }
            }

            // gutter (pinned lane labels + mask boxes) draws over the panning markers
            if (lanes) DrawLaneGutter(laneDefs);

            // playhead
            Image ph = MakeImg(content, "TLPlayhead", padLeft, 4f, 2f, H - 8f, new Color(0.93f, 0.42f, 0.26f, 1f), false);
            playhead = ph.rectTransform; dynamic.Add(ph.gameObject);

            ApplyPan();
            RefreshSelection();
        }

        void ApplyPan()
        {
            if (content == null || view == null) return;
            float over = content.sizeDelta.x - view.rect.width;
            float pan = (panSlider != null && over > 0f) ? panSlider.value : 0f;
            float off = Mathf.Max(0f, over) * pan;
            content.anchoredPosition = new Vector2(-off, content.anchoredPosition.y);
            // keep the lane gutter pinned to the view's left edge while the content pans
            for (int i = 0; i < gutterRows.Count; i++)
                if (gutterRows[i] != null)
                    gutterRows[i].anchoredPosition = new Vector2(off, gutterRows[i].anchoredPosition.y);
        }

        void RefreshSelection()
        {
            List<PoseKeyframe> ks = Keys();
            for (int i = 0; i < keyHandles.Count; i++)
            {
                Image im = keyHandles[i] != null ? keyHandles[i].GetComponent<Image>() : null;
                if (im == null) continue;
                bool sel = i == selIdx;
                im.color = sel ? new Color(1f, 0.78f, 0.25f, 1f) : new Color(0.36f, 0.83f, 0.66f, 1f);
                keyHandles[i].sizeDelta = sel ? new Vector2(20f, 20f) : new Vector2(16f, 16f);
            }
            PoseKeyframe k = (ks != null && selIdx >= 0 && selIdx < ks.Count) ? ks[selIdx] : null;
            suppress = true;
            if (keyText != null) keyText.text = k != null ? ("Key " + (selIdx + 1) + " of " + ks.Count) : "no key selected";
            if (secInput != null) secInput.text = k != null ? k.seconds.ToString("0.000", CultureInfo.InvariantCulture) : "";
            if (easeDrop != null && k != null)
            {
                int e = 0;
                if (k.ease == "smooth") e = 1; else if (k.ease == "in") e = 2; else if (k.ease == "out") e = 3;
                easeDrop.value = e; easeDrop.RefreshShownValue();
            }
            suppress = false;
        }

        // ---------------------------------------------------------------- interactions
        void OnScrub(BaseEventData d)
        {
            PointerEventData e = d as PointerEventData;
            if (e == null || boundItem == null) return;
            float lx;
            if (!PointerToContentX(e, out lx)) return;
            float cycle = Cycle();
            float t = Mathf.Clamp((lx - padLeft) / Mathf.Max(1f, pxPerSec), 0f, Mathf.Max(0f, cycle - 0.0001f));
            scrubbing = true;
            plugin.ApplierRef.SetPaused(boundItem, true);     // scrubbing pauses (Play resumes)
            plugin.ApplierRef.SetPhase(boundItem, t);
        }

        void OnKeyDown(int idx)
        {
            dragIdx = idx;
            if (selIdx != idx)
            {
                selIdx = idx;
                suppress = true; plugin.TimelineSelectKey(idx); suppress = false;
                RefreshSelection();
            }
        }

        void OnKeyDrag(int idx, PointerEventData e)
        {
            List<PoseKeyframe> ks = Keys();
            if (ks == null || idx <= 0 || idx >= ks.Count)
            { if (idx == 0) SetTlStatus("key 1 anchors the loop start — retime the others around it"); return; }
            float lx;
            if (!PointerToContentX(e, out lx)) return;
            float tPrev = KeyTime(idx - 1);
            float tNextOld = KeyTime(idx) + Mathf.Max(0.01f, ks[idx].seconds);   // fixed for middle keys
            float newT = SnapTime((lx - padLeft) / Mathf.Max(1f, pxPerSec));
            bool last = idx == ks.Count - 1;
            float hi = last ? tPrev + 30f : tNextOld - MIN_SEG;
            newT = Mathf.Clamp(newT, tPrev + MIN_SEG, hi);
            ks[idx - 1].seconds = newT - tPrev;
            if (!last) ks[idx].seconds = tNextOld - newT;      // keep the next key where it was
            // live-move this diamond (full rebuild on release)
            if (idx < keyHandles.Count && keyHandles[idx] != null)
                keyHandles[idx].anchoredPosition = new Vector2(padLeft + newT * pxPerSec, keyHandles[idx].anchoredPosition.y);
            if (secInput != null && selIdx == idx - 1) secInput.text = ks[idx - 1].seconds.ToString("0.000", CultureInfo.InvariantCulture);
            SetTlStatus("key " + (idx + 1) + " at " + newT.ToString("0.00") + "s");
        }

        void OnKeyUp(int idx)
        {
            if (dragIdx >= 0)
            {
                dragIdx = -1;
                plugin.TimelineRefreshOptionText();
                Rebuild();
            }
        }

        void OnPlayPause()
        {
            if (boundItem == null) return;
            bool p = plugin.ApplierRef.GetPaused(boundItem);
            plugin.ApplierRef.SetPaused(boundItem, !p);
            if (!boundItem.active) SetTlStatus("note: this item's toggle is OFF — turn it on to see the animation");
        }

        void OnSpeedSlider(float v)
        {
            if (suppress || boundItem == null) return;
            boundItem.speed = v;
            if (speedInput != null) { suppress = true; speedInput.text = v.ToString("0.00", CultureInfo.InvariantCulture); suppress = false; }
        }

        void OnSpeedTyped(string s)
        {
            if (suppress || boundItem == null) return;
            float v;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { SyncSpeedUI(); return; }
            boundItem.speed = Mathf.Clamp(v, 0f, 10f);
            SyncSpeedUI();
        }

        void SyncSpeedUI()
        {
            if (boundItem == null) return;
            suppress = true;
            if (speedSlider != null) speedSlider.value = Mathf.Clamp(boundItem.speed, speedSlider.minValue, speedSlider.maxValue);
            if (speedInput != null) speedInput.text = boundItem.speed.ToString("0.00", CultureInfo.InvariantCulture);
            suppress = false;
        }

        void OnSecondsTyped(string s)
        {
            if (suppress) return;
            List<PoseKeyframe> ks = Keys();
            if (ks == null || selIdx < 0 || selIdx >= ks.Count) return;
            float v;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { RefreshSelection(); return; }
            ks[selIdx].seconds = Mathf.Clamp(v, MIN_SEG, 120f);
            plugin.TimelineRefreshOptionText();
            Rebuild();
        }

        void OnEaseChanged(int v)
        {
            if (suppress) return;
            List<PoseKeyframe> ks = Keys();
            if (ks == null || selIdx < 0 || selIdx >= ks.Count) return;
            ks[selIdx].ease = v == 1 ? "smooth" : (v == 2 ? "in" : (v == 3 ? "out" : "linear"));
            SetTlStatus("segment " + (selIdx + 1) + " → " + (selIdx + 2 > ks.Count ? 1 : selIdx + 2) + " ease: " + (easeDrop != null ? easeDrop.options[v].text : ks[selIdx].ease));
        }

        void OnCapture()
        {
            if (selIdx < 0) { SetTlStatus("select a key first"); return; }
            plugin.TimelineCaptureIntoMasked(selIdx, MaskAllows);
            SetTlStatus("captured the current pose into key " + (selIdx + 1) + (maskOff.Count > 0 ? " (mask applied)" : ""));
            Rebuild();
        }

        void OnDelete()
        {
            if (selIdx < 0) { SetTlStatus("select a key first"); return; }
            plugin.TimelineRemoveKey(selIdx);
        }

        void OnAddAtPlayhead()
        {
            List<PoseKeyframe> ks = Keys();
            if (boundItem == null || ks == null || ks.Count == 0) return;
            float cycle = Cycle();
            float t = Mathf.Repeat(plugin.ApplierRef.GetPhase(boundItem), Mathf.Max(0.01f, cycle));
            // find the segment containing t
            int a = 0; float ta = 0f;
            for (int i = 0; i < ks.Count; i++)
            {
                float seg = Mathf.Max(0.01f, ks[i].seconds);
                if (t < ta + seg || i == ks.Count - 1) { a = i; break; }
                ta += seg;
            }
            float segLen = Mathf.Max(0.01f, ks[a].seconds);
            float into = Mathf.Clamp(t - ta, MIN_SEG, segLen - MIN_SEG);
            float u = into / segLen;
            int b = (a + 1) % ks.Count;

            PoseKeyframe nk = new PoseKeyframe();
            nk.seconds = segLen - into;
            nk.ease = ks[a].ease;
            nk.channels = InterpChannels(ks[a], ks[b], u);
            ks[a].seconds = into;
            plugin.TimelineInsertKey(a + 1, nk);
            SetTlStatus("added key at " + t.ToString("0.00") + "s (pose interpolated — animation unchanged)");
        }

        // Interpolate the two keys' channel lists so the inserted key reproduces the pose
        // the animation already showed at that moment.
        static List<KeyframeChannel> InterpChannels(PoseKeyframe ka, PoseKeyframe kb, float u)
        {
            List<KeyframeChannel> outCh = new List<KeyframeChannel>();
            HashSet<string> seen = new HashSet<string>();
            if (ka != null && ka.channels != null)
                for (int i = 0; i < ka.channels.Count; i++)
                {
                    KeyframeChannel a = ka.channels[i];
                    if (a == null || a.id == null) continue;
                    seen.Add(a.id);
                    KeyframeChannel b = KeyChannels.Find(kb, a.id);
                    outCh.Add(LerpChannel(a, b, u));
                }
            if (kb != null && kb.channels != null)
                for (int i = 0; i < kb.channels.Count; i++)
                {
                    KeyframeChannel b = kb.channels[i];
                    if (b == null || b.id == null || seen.Contains(b.id)) continue;
                    outCh.Add(LerpChannel(b, null, 0f));   // only in B: hold B's values
                }
            return outCh;
        }

        static Quaternion ChanQuat(KeyframeChannel c)
        {
            if (c.quat != null && c.quat.Length == 4) return new Quaternion(c.quat[0], c.quat[1], c.quat[2], c.quat[3]);
            float[] r = c.rotation != null && c.rotation.Length == 3 ? c.rotation : new float[] { 0f, 0f, 0f };
            return Quaternion.Euler(r[0], r[1], r[2]);
        }

        static KeyframeChannel LerpChannel(KeyframeChannel a, KeyframeChannel b, float u)
        {
            KeyframeChannel c = new KeyframeChannel();
            c.id = a.id;
            if (b == null) b = a;
            float[] ap = a.position != null ? a.position : new float[] { 0f, 0f, 0f };
            float[] bp = b.position != null ? b.position : ap;
            c.position = new float[] { Mathf.Lerp(ap[0], bp[0], u), Mathf.Lerp(ap[1], bp[1], u), Mathf.Lerp(ap[2], bp[2], u) };
            float[] asc = a.scale != null ? a.scale : new float[] { 1f, 1f, 1f };
            float[] bsc = b.scale != null ? b.scale : asc;
            c.scale = new float[] { Mathf.Lerp(asc[0], bsc[0], u), Mathf.Lerp(asc[1], bsc[1], u), Mathf.Lerp(asc[2], bsc[2], u) };
            Quaternion q = Quaternion.Slerp(ChanQuat(a), ChanQuat(b), u);
            c.quat = new float[] { q.x, q.y, q.z, q.w };
            Vector3 e = q.eulerAngles;
            c.rotation = new float[] { e.x, e.y, e.z };
            c.weight = Mathf.Lerp(a.weight, b.weight, u);
            return c;
        }

        // ---------------------------------------------------------------- P2: lanes
        // A lane is one row in the lanes view: either a channel GROUP (Bones/Meshes/
        // Blendshapes/IK, expandable) or one member target inside an expanded group.
        class LaneDef
        {
            public string label;
            public string cat;    // "bone" | "mesh" | "blend" | "ik"
            public string id;     // full channel id (member lanes only)
            public bool group;
        }

        static string ChanCat(string id)
        {
            int c = id != null ? id.IndexOf(':') : -1;
            return c > 0 ? id.Substring(0, c) : "";
        }

        static string ChanShort(string id)
        {
            if (id == null) return "?";
            int c = id.IndexOf(':');
            string body = c >= 0 ? id.Substring(c + 1) : id;
            int sep = body.IndexOf("::", StringComparison.Ordinal);
            return sep >= 0 ? body.Substring(sep + 2) : body;
        }

        static string CatTitle(string cat)
        {
            if (cat == "bone") return "Bones";
            if (cat == "mesh") return "Meshes";
            if (cat == "blend") return "Blendshapes";
            if (cat == "ik") return "IK";
            return cat;
        }

        List<LaneDef> ComputeLanes(List<PoseKeyframe> ks)
        {
            string[] cats = { "bone", "mesh", "blend", "ik" };
            Dictionary<string, List<string>> members = new Dictionary<string, List<string>>();
            for (int c = 0; c < cats.Length; c++) members[cats[c]] = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < ks.Count; i++)
            {
                PoseKeyframe k = ks[i];
                if (k == null || k.channels == null) continue;
                for (int j = 0; j < k.channels.Count; j++)
                {
                    KeyframeChannel ch = k.channels[j];
                    if (ch == null || ch.id == null || seen.Contains(ch.id)) continue;
                    seen.Add(ch.id);
                    string cat = ChanCat(ch.id);
                    if (members.ContainsKey(cat)) members[cat].Add(ch.id);
                }
            }
            List<LaneDef> outLanes = new List<LaneDef>();
            for (int c = 0; c < cats.Length; c++)
            {
                List<string> ids = members[cats[c]];
                if (ids.Count == 0) continue;
                bool open = expandedGroups.Contains(cats[c]);
                LaneDef g = new LaneDef();
                g.group = true; g.cat = cats[c];
                g.label = (open ? "- " : "+ ") + CatTitle(cats[c]) + " (" + ids.Count + ")";
                outLanes.Add(g);
                if (open)
                    for (int i = 0; i < ids.Count; i++)
                    {
                        LaneDef m = new LaneDef();
                        m.cat = cats[c]; m.id = ids[i];
                        m.label = "    " + ChanShort(ids[i]);
                        outLanes.Add(m);
                    }
            }
            return outLanes;
        }

        float LaneScrollPx(int count)
        {
            float area = LANE_BOT - (LANE_TOP + LANE_H);   // master row is pinned; the rest scroll
            float total = count * LANE_H;
            float scroll = laneScrollSlider != null ? laneScrollSlider.value : 0f;
            return Mathf.Max(0f, total - area) * scroll;
        }

        // Does key i CHANGE this lane's channel(s) vs the previous key (looping)?
        bool LaneChangedAt(List<PoseKeyframe> ks, LaneDef lane, int i, out bool present)
        {
            present = false;
            PoseKeyframe cur = ks[i];
            PoseKeyframe prev = ks[(i - 1 + ks.Count) % ks.Count];
            if (lane.group)
            {
                bool changed = false;
                if (cur != null && cur.channels != null)
                    for (int j = 0; j < cur.channels.Count; j++)
                    {
                        KeyframeChannel ch = cur.channels[j];
                        if (ch == null || ChanCat(ch.id) != lane.cat) continue;
                        present = true;
                        if (ks.Count > 1 && ChannelsDiffer(KeyChannels.Find(prev, ch.id), ch)) { changed = true; break; }
                    }
                return changed;
            }
            KeyframeChannel c2 = KeyChannels.Find(cur, lane.id);
            present = c2 != null;
            return ks.Count > 1 && ChannelsDiffer(KeyChannels.Find(prev, lane.id), c2);
        }

        void DrawLaneMarkers(List<PoseKeyframe> ks, List<LaneDef> lanesList)
        {
            float scrollPx = LaneScrollPx(lanesList.Count);
            bool sparse = ks.Count > 60;   // huge imports: only draw the keys that change the lane
            for (int L = 0; L < lanesList.Count; L++)
            {
                float rowY = LANE_TOP + LANE_H + L * LANE_H - scrollPx;
                if (rowY < LANE_TOP + LANE_H - 0.5f || rowY + LANE_H > LANE_BOT + 0.5f) continue;
                LaneDef lane = lanesList[L];
                Image rbg = MakeImg(content, "TLLaneBg" + L, 0f, rowY, content.sizeDelta.x,
                    LANE_H - 1f, new Color(0.16f, 0.17f, 0.22f, (L % 2 == 0) ? 0.85f : 0.6f), false);
                dynamic.Add(rbg.gameObject);
                for (int i = 0; i < ks.Count; i++)
                {
                    bool present;
                    bool changed = LaneChangedAt(ks, lane, i, out present);
                    if (!present || (sparse && !changed)) continue;
                    int idx = i;
                    float kx = padLeft + KeyTime(i) * pxPerSec;
                    float sz = changed ? 9f : 5f;
                    GameObject mgo = new GameObject("TLM", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    Image mim = mgo.GetComponent<Image>();
                    mim.color = changed ? new Color(0.36f, 0.83f, 0.66f, 1f) : new Color(1f, 1f, 1f, 0.28f);
                    mim.raycastTarget = true;
                    RectTransform mrt = PlaceRT(mgo, content, 0f, 0f, sz, sz);
                    mrt.pivot = new Vector2(0.5f, 0.5f);
                    mrt.anchoredPosition = new Vector2(kx, -(rowY + LANE_H * 0.5f));
                    if (changed) mrt.localRotation = Quaternion.Euler(0f, 0f, 45f);
                    dynamic.Add(mgo);
                    AddTrigger(mgo, EventTriggerType.PointerDown, delegate(BaseEventData d) { OnKeyDown(idx); });
                }
            }
        }

        void DrawLaneGutter(List<LaneDef> lanesList)
        {
            MakeGutterRow(LANE_TOP, "All keys (drag to retime)", null);
            float scrollPx = LaneScrollPx(lanesList.Count);
            for (int L = 0; L < lanesList.Count; L++)
            {
                float rowY = LANE_TOP + LANE_H + L * LANE_H - scrollPx;
                if (rowY < LANE_TOP + LANE_H - 0.5f || rowY + LANE_H > LANE_BOT + 0.5f) continue;
                MakeGutterRow(rowY, lanesList[L].label, lanesList[L]);
            }
        }

        void MakeGutterRow(float rowY, string label, LaneDef lane)
        {
            GameObject row = new GameObject("TLGut", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            Image rim = row.GetComponent<Image>();
            rim.color = new Color(0.10f, 0.11f, 0.14f, 0.97f);
            rim.raycastTarget = lane != null && lane.group;   // group rows click to expand/collapse
            RectTransform rrt = PlaceRT(row, content, 0f, rowY, GUT_W, LANE_H - 1f);
            dynamic.Add(row); gutterRows.Add(rrt);
            MakeTxt(row.transform, label, 4f, 1f, GUT_W - 22f, LANE_H - 2f, 10, TextAnchor.MiddleLeft,
                new Color(1f, 1f, 1f, lane != null && !lane.group ? 0.75f : 0.95f));
            if (lane == null) return;
            if (lane.group)
            {
                string cat = lane.cat;
                AddTrigger(row, EventTriggerType.PointerDown, delegate(BaseEventData d)
                {
                    if (expandedGroups.Contains(cat)) expandedGroups.Remove(cat); else expandedGroups.Add(cat);
                    Rebuild();
                });
            }
            // capture/paste mask box: green = included, red = excluded
            string maskKey = lane.group ? lane.cat : lane.id;
            bool allowed = lane.group ? !maskOff.Contains(lane.cat) : MaskAllows(lane.id);
            LaneDef laneRef = lane;
            GameObject box = new GameObject("TLMask", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            Image bim = box.GetComponent<Image>();
            bim.color = allowed ? new Color(0.36f, 0.83f, 0.66f, 0.95f) : new Color(0.6f, 0.25f, 0.25f, 0.95f);
            bim.raycastTarget = true;
            PlaceRT(box, row.transform, GUT_W - 16f, (LANE_H - 11f) * 0.5f, 10f, 10f);
            AddTrigger(box, EventTriggerType.PointerDown, delegate(BaseEventData d)
            {
                if (!laneRef.group && maskOff.Contains(laneRef.cat))
                { SetTlStatus("its whole group is excluded — click the group's box first"); return; }
                if (maskOff.Contains(maskKey)) maskOff.Remove(maskKey); else maskOff.Add(maskKey);
                SetTlStatus((maskOff.Contains(maskKey) ? "excluded from" : "included in") + " capture/paste: " + label.Trim());
                Rebuild();
            });
        }

        // ---------------------------------------------------------------- P2: mask
        public bool MaskAllows(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (maskOff.Contains(id)) return false;
            return !maskOff.Contains(ChanCat(id));
        }

        void OnMaskAll()
        {
            maskOff.Clear();
            Rebuild();
            SetTlStatus("capture/paste mask: everything included");
        }

        void OnMaskNone()
        {
            maskOff.Add("bone"); maskOff.Add("mesh"); maskOff.Add("blend"); maskOff.Add("ik");
            Rebuild();
            SetTlStatus("capture/paste mask: everything excluded — tick lanes back on in the lanes view");
        }

        // ---------------------------------------------------------------- P2: copy/paste
        static KeyframeChannel CloneChan(KeyframeChannel s)
        {
            KeyframeChannel c = new KeyframeChannel();
            c.id = s.id;
            c.position = s.position != null ? (float[])s.position.Clone() : null;
            c.rotation = s.rotation != null ? (float[])s.rotation.Clone() : null;
            c.quat = s.quat != null ? (float[])s.quat.Clone() : null;
            c.scale = s.scale != null ? (float[])s.scale.Clone() : null;
            c.weight = s.weight;
            return c;
        }

        static PoseKeyframe DeepCloneKey(PoseKeyframe src)
        {
            PoseKeyframe k = new PoseKeyframe();
            k.seconds = src.seconds;
            k.ease = src.ease;
            k.channels = new List<KeyframeChannel>();
            if (src.channels != null)
                for (int i = 0; i < src.channels.Count; i++)
                    if (src.channels[i] != null) k.channels.Add(CloneChan(src.channels[i]));
            return k;
        }

        static bool D3(float[] a, float[] b, float fb, float eps)
        {
            for (int i = 0; i < 3; i++)
            {
                float av = a != null && i < a.Length ? a[i] : fb;
                float bv = b != null && i < b.Length ? b[i] : fb;
                if (Mathf.Abs(av - bv) > eps) return true;
            }
            return false;
        }

        static bool ChannelsDiffer(KeyframeChannel a, KeyframeChannel b)
        {
            if (a == null && b == null) return false;
            if (a == null || b == null) return true;
            if (D3(a.position, b.position, 0f, 0.0005f)) return true;
            if (D3(a.scale, b.scale, 1f, 0.0005f)) return true;
            if (Mathf.Abs(a.weight - b.weight) > 0.01f) return true;
            return Quaternion.Angle(ChanQuat(a), ChanQuat(b)) > 0.05f;
        }

        void OnCopy()
        {
            List<PoseKeyframe> ks = Keys();
            if (ks == null || selIdx < 0 || selIdx >= ks.Count) { SetTlStatus("select a key first"); return; }
            clipboard = DeepCloneKey(ks[selIdx]);
            int n = clipboard.channels != null ? clipboard.channels.Count : 0;
            SetTlStatus("copied key " + (selIdx + 1) + " (" + n + " channels) — paste works across items too");
        }

        void OnPasteInto()
        {
            List<PoseKeyframe> ks = Keys();
            if (clipboard == null) { SetTlStatus("copy a key first"); return; }
            if (ks == null || selIdx < 0 || selIdx >= ks.Count) { SetTlStatus("select a key first"); return; }
            PoseKeyframe key = ks[selIdx];
            List<KeyframeChannel> keep = new List<KeyframeChannel>();
            if (key.channels != null)
                for (int i = 0; i < key.channels.Count; i++)
                {
                    KeyframeChannel c = key.channels[i];
                    if (c != null && !MaskAllows(c.id)) keep.Add(c);   // masked-out channels keep their old values
                }
            if (clipboard.channels != null)
                for (int i = 0; i < clipboard.channels.Count; i++)
                {
                    KeyframeChannel c = clipboard.channels[i];
                    if (c != null && MaskAllows(c.id)) keep.Add(CloneChan(c));
                }
            key.channels = keep;
            key.ease = clipboard.ease;
            plugin.TimelineSelectKey(selIdx);   // re-push the main window's sliders
            Rebuild();
            SetTlStatus("pasted into key " + (selIdx + 1) + (maskOff.Count > 0 ? " (mask applied)" : ""));
        }

        void OnPasteNew()
        {
            if (clipboard == null) { SetTlStatus("copy a key first"); return; }
            List<PoseKeyframe> ks = Keys();
            if (boundItem == null || ks == null) return;
            int at = selIdx >= 0 ? selIdx + 1 : ks.Count;
            plugin.TimelineInsertKey(at, DeepCloneKey(clipboard));
            SetTlStatus("pasted a new key at slot " + (at + 1));
        }

        void SetTlStatus(string s) { if (statusText != null) statusText.text = s; }

        // ---------------------------------------------------------------- per frame
        public void Frame()
        {
            if (!wired || window == null || !window.activeSelf) return;
            PoseItem cur = plugin.CurrentItem;
            if (cur != boundItem || cur == null || !cur.useKeyframes)
            {
                // the main window moved to another item — follow it (or close if unusable)
                if (cur != null && cur.type == "animation" && cur.useKeyframes)
                { boundItem = cur; selIdx = plugin.CurrentKeyIndex(); Rebuild(); }
                else { Hide(); return; }
            }
            List<PoseKeyframe> ks = Keys();
            if (ks == null) { Hide(); return; }
            if (ks.Count != builtKeyCount) { selIdx = Mathf.Clamp(selIdx, 0, ks.Count - 1); Rebuild(); }

            float cycle = Cycle();
            float phase = Mathf.Repeat(plugin.ApplierRef.GetPhase(boundItem), Mathf.Max(0.01f, cycle));
            if (playhead != null)
                playhead.anchoredPosition = new Vector2(padLeft + phase * pxPerSec, playhead.anchoredPosition.y);
            if (timeText != null)
                timeText.text = phase.ToString("00.000", CultureInfo.InvariantCulture) + " / " + cycle.ToString("00.000", CultureInfo.InvariantCulture) + "s";
            if (playBtnText != null)
                playBtnText.text = plugin.ApplierRef.GetPaused(boundItem) ? "Play" : "Pause";
        }
    }
}
