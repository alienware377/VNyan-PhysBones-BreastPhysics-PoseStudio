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
            dynamic.Clear(); keyHandles.Clear();
            playhead = null; scrubPad = null; stripBar = null;

            PoseItem it = boundItem;
            List<PoseKeyframe> ks = Keys();
            if (it == null || ks == null || ks.Count == 0 || content == null || view == null)
            { builtKeyCount = -1; return; }
            builtKeyCount = ks.Count;
            if (title != null) title.text = "Timeline — " + it.name;

            float cycle = Cycle();
            float viewW = view.rect.width;
            if (viewW < 50f) viewW = 860f;
            float zoom = zoomSlider != null ? Mathf.Max(1f, zoomSlider.value) : 1f;
            pxPerSec = Mathf.Max(20f, (viewW - 2f * PAD) / Mathf.Max(0.05f, cycle)) * zoom;
            float contentW = cycle * pxPerSec + 2f * PAD;
            content.sizeDelta = new Vector2(contentW, content.sizeDelta.y);

            float H = content.sizeDelta.y;            // ~130
            float stripTop = 26f, stripH = 64f;
            float diamondY = stripTop + stripH * 0.5f;

            // scrub pad: whole-content press/drag surface (behind everything else)
            Image pad = MakeImg(content, "TLScrubPad", 0f, 0f, contentW, H, new Color(1f, 1f, 1f, 0.004f), true);
            scrubPad = pad.rectTransform; dynamic.Add(pad.gameObject);
            AddTrigger(pad.gameObject, EventTriggerType.PointerDown, OnScrub);
            AddTrigger(pad.gameObject, EventTriggerType.Drag, OnScrub);
            AddTrigger(pad.gameObject, EventTriggerType.PointerUp, delegate(BaseEventData d) { scrubbing = false; });

            // strip bar
            Image bar = MakeImg(content, "TLStrip", PAD, stripTop, contentW - 2f * PAD, stripH, new Color(0.28f, 0.26f, 0.45f, 1f), false);
            stripBar = bar.rectTransform; dynamic.Add(bar.gameObject);

            // ruler ticks + labels
            int lblStep = cycle > 120f ? 10 : (cycle > 40f ? 5 : 1);
            bool halfTicks = pxPerSec > 46f;
            for (float t = 0f; t <= cycle + 0.001f; t += halfTicks ? 0.5f : 1f)
            {
                bool whole = Mathf.Abs(t - Mathf.Round(t)) < 0.01f;
                float x = PAD + t * pxPerSec;
                Image tick = MakeImg(content, "tick", x, whole ? 8f : 14f, 1f, whole ? 16f : 10f, new Color(1f, 1f, 1f, whole ? 0.55f : 0.28f), false);
                dynamic.Add(tick.gameObject);
                if (whole && ((int)Mathf.Round(t)) % lblStep == 0)
                {
                    Text lbl = MakeTxt(content, Mathf.Round(t).ToString("0") + "s", x + 3f, 2f, 40f, 14f, 10, TextAnchor.UpperLeft, new Color(1f, 1f, 1f, 0.75f));
                    dynamic.Add(lbl.gameObject);
                }
            }

            // key diamonds
            for (int i = 0; i < ks.Count; i++)
            {
                int idx = i;   // C#5: capture a copy for the closures below
                float kx = PAD + KeyTime(i) * pxPerSec;
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

                Text klbl = MakeTxt(content, (i + 1).ToString(), kx - 20f, stripTop + stripH + 6f, 40f, 14f, 10, TextAnchor.UpperCenter, new Color(1f, 1f, 1f, 0.8f));
                dynamic.Add(klbl.gameObject);
            }

            // playhead
            Image ph = MakeImg(content, "TLPlayhead", PAD, 4f, 2f, H - 8f, new Color(0.93f, 0.42f, 0.26f, 1f), false);
            playhead = ph.rectTransform; dynamic.Add(ph.gameObject);

            ApplyPan();
            RefreshSelection();
        }

        void ApplyPan()
        {
            if (content == null || view == null) return;
            float over = content.sizeDelta.x - view.rect.width;
            float pan = (panSlider != null && over > 0f) ? panSlider.value : 0f;
            content.anchoredPosition = new Vector2(-Mathf.Max(0f, over) * pan, content.anchoredPosition.y);
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
            float t = Mathf.Clamp((lx - PAD) / Mathf.Max(1f, pxPerSec), 0f, Mathf.Max(0f, cycle - 0.0001f));
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
            float newT = SnapTime((lx - PAD) / Mathf.Max(1f, pxPerSec));
            bool last = idx == ks.Count - 1;
            float hi = last ? tPrev + 30f : tNextOld - MIN_SEG;
            newT = Mathf.Clamp(newT, tPrev + MIN_SEG, hi);
            ks[idx - 1].seconds = newT - tPrev;
            if (!last) ks[idx].seconds = tNextOld - newT;      // keep the next key where it was
            // live-move this diamond (full rebuild on release)
            if (idx < keyHandles.Count && keyHandles[idx] != null)
                keyHandles[idx].anchoredPosition = new Vector2(PAD + newT * pxPerSec, keyHandles[idx].anchoredPosition.y);
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
            plugin.TimelineCaptureInto(selIdx);
            SetTlStatus("captured the item's current pose into key " + (selIdx + 1));
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
                playhead.anchoredPosition = new Vector2(PAD + phase * pxPerSec, playhead.anchoredPosition.y);
            if (timeText != null)
                timeText.text = phase.ToString("00.000", CultureInfo.InvariantCulture) + " / " + cycle.ToString("00.000", CultureInfo.InvariantCulture) + "s";
            if (playBtnText != null)
                playBtnText.text = plugin.ApplierRef.GetPaused(boundItem) ? "Play" : "Pause";
        }
    }
}
