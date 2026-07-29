using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PoseStudio
{
    // ---------------------------------------------------------------------------
    // Import an external animation (MMD .vmd, .bvh, VRM Animation .vrma/.glb/.gltf)
    // and TRANSLATE it onto the bound avatar's humanoid rig, producing a Pose Studio
    // keyframe animation item (quaternion channels, resampled to 30 fps).
    //
    // The retarget is name-based: each source bone is mapped to a Unity HumanBodyBones
    // (MMD Japanese names, BVH/Mixamo aliases, or VRMA's humanoid map), resolved to the
    // avatar's transform via the Animator. The source's LOCAL rotation (delta from its own
    // rest) is coordinate-converted and applied as the avatar bone's local offset on top of
    // its rest — the same "apply converted local rotation" scheme MMD→VRM tools use.
    //
    // Coordinate conversions live in one place (CONVERSION KNOBS) and are trivially
    // flippable, because getting a source rig's handedness/forward exactly right usually
    // takes a look-and-flip pass (like axis calibration elsewhere in this project).
    // ---------------------------------------------------------------------------
    public static class AnimImport
    {
        // ================= CONVERSION KNOBS (flip if an import comes in mirrored/rotated) =====
        // Each converts a source-space LOCAL quaternion to Unity space. Implemented as a
        // per-component sign mask on (x,y,z,w). Identity = {1,1,1,1}.
        static readonly float[] MMD_SIGN  = { 1f, 1f, -1f, -1f }; // MMD (LH, faces -Z) -> Unity
        static readonly float[] BVH_SIGN  = { -1f, 1f, 1f, -1f }; // BVH (RH, Y-up) -> Unity (LH)
        static readonly float[] VRMA_SIGN = { 1f, 1f, 1f, 1f };   // VRM 1.0 already Unity-ish
        const float MMD_FPS = 30f;
        const float OUT_FPS = 30f;
        const int   MAX_KEYS = 1200;   // safety cap on output keyframes
        // =====================================================================================

        public static PoseItem Import(string path, GameObject avatar, Animator anim, out string report)
        {
            report = "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { report = "file not found"; return null; }
            if (avatar == null || anim == null || !anim.isHuman) { report = "need a humanoid avatar bound"; return null; }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".vmd")  return ImportVMD(path, avatar, anim, out report);
                if (ext == ".bvh")  return ImportBVH(path, avatar, anim, out report);
                if (ext == ".vrma" || ext == ".glb" || ext == ".gltf") return ImportVRMA(path, avatar, anim, out report);
            }
            catch (Exception e) { report = "import failed: " + e.Message; return null; }
            report = "unsupported format '" + ext + "' (use .vmd, .bvh, .vrma)";
            return null;
        }

        // ---------- MMD live-adjust: presets + parsed data + rebuildable convert ----------
        // Quaternion component sign masks (x,y,z,w). The right one depends on the .vmd's
        // coordinate convention; the in-plugin dialog lets the user cycle these live.
        public static readonly float[][] MMD_PRESETS = {
            new float[] { 1f, 1f, -1f, -1f },   // 0 default (flip Z+W)
            new float[] { -1f, -1f, 1f, 1f },   // 1 flip X+Y
            new float[] { 1f, -1f, -1f, 1f },   // 2 flip Y+Z
            new float[] { -1f, 1f, 1f, -1f },   // 3 flip X+W
            new float[] { 1f, 1f, 1f, 1f },     // 4 raw (no conversion)
        };
        public static int MmdPresetCount { get { return MMD_PRESETS.Length; } }

        public class MmdOpts { public int preset = 0; public bool mirror = false; public bool face180 = false; public bool layFlat = false; }

        // Parsed VMD (RAW rotations, before any coordinate conversion) so the dialog can
        // re-convert without re-reading the file.
        public class VmdRaw
        {
            public string name;
            public float duration;
            public Dictionary<HumanBodyBones, Track> tracks = new Dictionary<HumanBodyBones, Track>();
            public List<BlendTrack> morphs = new List<BlendTrack>();
            public int mappedBones, unknownBones; public string sample = ""; public bool sjOk = true;
        }

        static HumanBodyBones Opposite(HumanBodyBones hb)
        {
            string s = hb.ToString();
            if (s.StartsWith("Left")) s = "Right" + s.Substring(4);
            else if (s.StartsWith("Right")) s = "Left" + s.Substring(5);
            else return hb;
            HumanBodyBones o; return Enum.TryParse<HumanBodyBones>(s, out o) ? o : hb;
        }

        // ---------- shared: per-bone rotation track + resampling ----------
        public class Track
        {
            public HumanBodyBones hb;
            public List<float> t = new List<float>();      // seconds, ascending
            public List<Quaternion> q = new List<Quaternion>();
            public void Add(float time, Quaternion rot)
            {
                // keep ascending; VMD keys can arrive unsorted
                int i = t.Count - 1;
                if (i >= 0 && time <= t[i]) { if (time == t[i]) { q[i] = rot; return; } }
                t.Add(time); q.Add(rot);
            }
            public void Sort()
            {
                for (int a = 1; a < t.Count; a++)
                {
                    float kt = t[a]; Quaternion kq = q[a]; int b = a - 1;
                    while (b >= 0 && t[b] > kt) { t[b + 1] = t[b]; q[b + 1] = q[b]; b--; }
                    t[b + 1] = kt; q[b + 1] = kq;
                }
            }
            public Quaternion Sample(float time)
            {
                int n = t.Count;
                if (n == 0) return Quaternion.identity;
                if (time <= t[0]) return q[0];
                if (time >= t[n - 1]) return q[n - 1];
                int lo = 0, hi = n - 1;
                while (hi - lo > 1) { int m = (lo + hi) / 2; if (t[m] <= time) lo = m; else hi = m; }
                float span = t[hi] - t[lo];
                float u = span > 1e-6f ? (time - t[lo]) / span : 0f;
                return Quaternion.Slerp(q[lo], q[hi], u);
            }
        }

        public class BlendTrack { public string shape; public List<float> t = new List<float>(); public List<float> w = new List<float>();
            public void Add(float time, float weight){ t.Add(time); w.Add(weight); }
            public float Sample(float time){ int n=t.Count; if(n==0)return 0f; if(time<=t[0])return w[0]; if(time>=t[n-1])return w[n-1];
                for(int i=1;i<n;i++){ if(t[i]>=time){ float s=t[i]-t[i-1]; float u=s>1e-6f?(time-t[i-1])/s:0f; return Mathf.Lerp(w[i-1],w[i],u);} } return w[n-1]; } }

        static PoseItem BuildItem(string name, Dictionary<HumanBodyBones, Track> tracks,
                                  List<BlendTrack> morphs, float duration, Animator anim)
        {
            PoseItem it = new PoseItem();
            it.name = name; it.type = "animation"; it.active = false; it.useKeyframes = true; it.blendTime = 0.3f;
            it.bones = new List<BoneTarget>();
            it.blendshapes = new List<BlendTarget>();

            List<HumanBodyBones> used = new List<HumanBodyBones>();
            foreach (KeyValuePair<HumanBodyBones, Track> kv in tracks)
            {
                if (anim.GetBoneTransform(kv.Key) == null) continue;   // rig lacks this bone
                kv.Value.Sort();
                used.Add(kv.Key);
                BoneTarget bt = new BoneTarget();
                bt.bone = kv.Key.ToString(); bt.useRotation = true; bt.usePosition = false; bt.useScale = false;
                it.bones.Add(bt);
            }
            foreach (BlendTrack m in morphs)
            {
                BlendTarget b = new BlendTarget(); b.mesh = ""; b.shape = m.shape; b.weight = 100f;
                it.blendshapes.Add(b);
            }

            float dt = 1f / OUT_FPS;
            int frames = Mathf.Clamp(Mathf.CeilToInt(duration * OUT_FPS) + 1, 1, MAX_KEYS);
            it.keyframes = new List<PoseKeyframe>();
            for (int f = 0; f < frames; f++)
            {
                float time = f * dt;
                PoseKeyframe kf = new PoseKeyframe();
                kf.seconds = (float)Math.Round(dt, 4);
                kf.channels = new List<KeyframeChannel>();
                for (int u = 0; u < used.Count; u++)
                {
                    HumanBodyBones hb = used[u];
                    Quaternion q = tracks[hb].Sample(time);
                    KeyframeChannel c = new KeyframeChannel();
                    c.id = KeyChannels.BoneId(hb.ToString());
                    c.quat = new float[] { q.x, q.y, q.z, q.w };
                    kf.channels.Add(c);
                }
                for (int mi = 0; mi < morphs.Count; mi++)
                {
                    KeyframeChannel c = new KeyframeChannel();
                    c.id = KeyChannels.BlendId(it.blendshapes[mi].mesh, morphs[mi].shape);
                    c.weight = Mathf.Clamp(morphs[mi].Sample(time) * 100f, 0f, 100f);
                    kf.channels.Add(c);
                }
                it.keyframes.Add(kf);
            }
            return it;
        }

        static Quaternion Conv(Quaternion q, float[] s)
        {
            return new Quaternion(q.x * s[0], q.y * s[1], q.z * s[2], q.w * s[3]);
        }

        // ================================ MMD .vmd ================================
        static PoseItem ImportVMD(string path, GameObject avatar, Animator anim, out string report)
        {
            VmdRaw raw = ParseVmd(path, out report);
            if (raw == null) return null;
            return BuildVmd(raw, new MmdOpts(), anim);
        }

        // Parse a .vmd into RAW (unconverted) rotation tracks + morphs — the dialog re-converts.
        public static VmdRaw ParseVmd(string path, out string report)
        {
            report = "";
            byte[] d = File.ReadAllBytes(path);
            Encoding sj = ShiftJIS();
            VmdRaw raw = new VmdRaw();
            raw.name = Path.GetFileNameWithoutExtension(path);
            raw.sjOk = sj != null;
            int p = 30 + 20;                                  // signature + model name
            uint boneCount = BitConverter.ToUInt32(d, p); p += 4;
            HashSet<string> unknownNames = new HashSet<string>();
            float maxT = 0f;
            for (uint i = 0; i < boneCount; i++)
            {
                string name = ReadFixedStr(d, p, 15, sj); p += 15;
                uint frame = BitConverter.ToUInt32(d, p); p += 4;
                p += 12;                                       // position — skipped
                Quaternion q = new Quaternion(
                    BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4),
                    BitConverter.ToSingle(d, p + 8), BitConverter.ToSingle(d, p + 12)); p += 16;
                p += 64;                                       // interpolation bezier
                HumanBodyBones hb;
                if (MMD.TryGetValue(name, out hb))
                {
                    Track tr; if (!raw.tracks.TryGetValue(hb, out tr)) { tr = new Track(); tr.hb = hb; raw.tracks[hb] = tr; }
                    tr.Add(frame / MMD_FPS, q);                // RAW — no conversion yet
                    raw.mappedBones++;
                    if (frame / MMD_FPS > maxT) maxT = frame / MMD_FPS;
                }
                else { raw.unknownBones++; if (unknownNames.Count < 12) unknownNames.Add(name); }
            }
            Dictionary<string, BlendTrack> mmap = new Dictionary<string, BlendTrack>();
            if (p + 4 <= d.Length)
            {
                uint morphCount = BitConverter.ToUInt32(d, p); p += 4;
                for (uint i = 0; i < morphCount && p + 23 <= d.Length; i++)
                {
                    string name = ReadFixedStr(d, p, 15, sj); p += 15;
                    uint frame = BitConverter.ToUInt32(d, p); p += 4;
                    float w = BitConverter.ToSingle(d, p); p += 4;
                    BlendTrack bt; if (!mmap.TryGetValue(name, out bt)) { bt = new BlendTrack(); bt.shape = name; mmap[name] = bt; raw.morphs.Add(bt); }
                    bt.Add(frame / MMD_FPS, w);
                    if (frame / MMD_FPS > maxT) maxT = frame / MMD_FPS;
                }
            }
            raw.duration = maxT;
            foreach (string s in unknownNames) raw.sample += (raw.sample.Length > 0 ? ", " : "") + s;
            return raw;
        }

        // Convert parsed VMD to a Pose Studio item with the chosen orientation options.
        public static PoseItem BuildVmd(VmdRaw raw, MmdOpts opts, Animator anim)
        {
            float[] mask = MMD_PRESETS[Mathf.Clamp(opts.preset, 0, MMD_PRESETS.Length - 1)];
            Quaternion root = Quaternion.identity;
            if (opts.face180) root = Quaternion.AngleAxis(180f, Vector3.up) * root;
            if (opts.layFlat) root = Quaternion.AngleAxis(90f, Vector3.right) * root;

            Dictionary<HumanBodyBones, Track> outTracks = new Dictionary<HumanBodyBones, Track>();
            foreach (KeyValuePair<HumanBodyBones, Track> kv in raw.tracks)
            {
                HumanBodyBones dst = opts.mirror ? Opposite(kv.Key) : kv.Key;
                Track src = kv.Value;
                Track ot; if (!outTracks.TryGetValue(dst, out ot)) { ot = new Track(); ot.hb = dst; outTracks[dst] = ot; }
                for (int k = 0; k < src.t.Count; k++)
                {
                    Quaternion q = Conv(src.q[k], mask);
                    if (opts.mirror) q = new Quaternion(q.x, -q.y, -q.z, q.w);   // reflect across sagittal plane
                    if (kv.Key == HumanBodyBones.Hips) q = root * q;              // whole-body reorient
                    ot.Add(src.t[k], q);
                }
            }
            PoseItem it = BuildItem(raw.name, outTracks, raw.morphs, raw.duration, anim);
            return it;
        }

        // ================================ .bvh ================================
        static PoseItem ImportBVH(string path, GameObject avatar, Animator anim, out string report)
        {
            string[] lines = File.ReadAllLines(path);
            // parse hierarchy: joint name + channel list (order matters), and MOTION rows
            List<string> jointNames = new List<string>();
            List<string[]> jointChannels = new List<string[]>();
            int li = 0; int frames = 0; float frameTime = 1f / 30f;
            for (; li < lines.Length; li++)
            {
                string ln = lines[li].Trim();
                if (ln.StartsWith("ROOT") || ln.StartsWith("JOINT"))
                {
                    string nm = ln.Substring(ln.IndexOf(' ') + 1).Trim();
                    jointNames.Add(nm); jointChannels.Add(new string[0]);
                }
                else if (ln.StartsWith("CHANNELS"))
                {
                    string[] tok = ln.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int cnt = int.Parse(tok[1]);
                    string[] ch = new string[cnt];
                    for (int c = 0; c < cnt; c++) ch[c] = tok[2 + c];
                    jointChannels[jointNames.Count - 1] = ch;
                }
                else if (ln.StartsWith("MOTION")) break;
            }
            for (; li < lines.Length; li++)
            {
                string ln = lines[li].Trim();
                if (ln.StartsWith("Frames:")) frames = int.Parse(ln.Substring(7).Trim());
                else if (ln.StartsWith("Frame Time:")) frameTime = float.Parse(ln.Substring(11).Trim(), CultureInfo.InvariantCulture);
                else if (ln.StartsWith("MOTION")) continue;
                else if (ln.Length > 0 && (char.IsDigit(ln[0]) || ln[0] == '-')) { break; }
            }
            // channel layout: flat index per joint
            int total = 0; int[] chStart = new int[jointNames.Count];
            for (int j = 0; j < jointNames.Count; j++) { chStart[j] = total; total += jointChannels[j].Length; }

            Dictionary<HumanBodyBones, Track> tracks = new Dictionary<HumanBodyBones, Track>();
            int row = 0;
            for (; li < lines.Length && row < frames; li++)
            {
                string ln = lines[li].Trim(); if (ln.Length == 0) continue;
                string[] v = ln.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (v.Length < total) continue;
                float time = row * frameTime;
                for (int j = 0; j < jointNames.Count; j++)
                {
                    HumanBodyBones hb;
                    if (!BvhBone(jointNames[j], out hb)) continue;
                    if (anim.GetBoneTransform(hb) == null) continue;
                    Quaternion q = BvhRot(jointChannels[j], v, chStart[j]);
                    Track tr; if (!tracks.TryGetValue(hb, out tr)) { tr = new Track(); tr.hb = hb; tracks[hb] = tr; }
                    tr.Add(time, Conv(q, BVH_SIGN));
                }
                row++;
            }
            float dur = frames * frameTime;
            PoseItem it = BuildItem(Path.GetFileNameWithoutExtension(path), tracks, new List<BlendTrack>(), dur, anim);
            report = "BVH: mapped " + tracks.Count + " joints, " + frames + " frames -> " + it.keyframes.Count + " keyframes (" + dur.ToString("0.0") + "s).";
            return it;
        }

        static Quaternion BvhRot(string[] channels, string[] vals, int start)
        {
            // accumulate the rotation in the file's channel order (Euler, degrees)
            Quaternion q = Quaternion.identity;
            for (int c = 0; c < channels.Length; c++)
            {
                string ch = channels[c];
                float a; if (!float.TryParse(vals[start + c], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) continue;
                if (ch == "Xrotation") q = q * Quaternion.AngleAxis(a, Vector3.right);
                else if (ch == "Yrotation") q = q * Quaternion.AngleAxis(a, Vector3.up);
                else if (ch == "Zrotation") q = q * Quaternion.AngleAxis(a, Vector3.forward);
            }
            return q;
        }

        // ================================ VRMA / glTF ================================
        static PoseItem ImportVRMA(string path, GameObject avatar, Animator anim, out string report)
        {
            byte[] bytes = File.ReadAllBytes(path);
            JObject gltf; byte[] bin;
            ParseGltf(bytes, out gltf, out bin);

            // VRMC_vrm_animation.humanoid.humanBones : { hips: {node: i}, ... }
            Dictionary<int, HumanBodyBones> nodeToHb = new Dictionary<int, HumanBodyBones>();
            JToken hb = gltf.SelectToken("extensions.VRMC_vrm_animation.humanoid.humanBones");
            if (hb != null)
                foreach (JProperty prop in ((JObject)hb).Properties())
                {
                    HumanBodyBones bone; if (!VrmHuman(prop.Name, out bone)) continue;
                    int node = prop.Value.Value<int?>("node") ?? -1;
                    if (node >= 0) nodeToHb[node] = bone;
                }

            JArray accessors = (JArray)gltf["accessors"];
            JArray views = (JArray)gltf["bufferViews"];
            Dictionary<HumanBodyBones, Track> tracks = new Dictionary<HumanBodyBones, Track>();
            float maxT = 0f;
            JArray anims = (JArray)gltf["animations"];
            if (anims != null && anims.Count > 0)
            {
                JObject a0 = (JObject)anims[0];
                JArray channels = (JArray)a0["channels"]; JArray samplers = (JArray)a0["samplers"];
                foreach (JToken chT in channels)
                {
                    JObject ch = (JObject)chT;
                    string pathName = ch.SelectToken("target.path") != null ? (string)ch.SelectToken("target.path") : "";
                    if (pathName != "rotation") continue;
                    int node = ch.SelectToken("target.node").Value<int>();
                    HumanBodyBones bone; if (!nodeToHb.TryGetValue(node, out bone)) continue;
                    if (anim.GetBoneTransform(bone) == null) continue;
                    JObject sm = (JObject)samplers[ch["sampler"].Value<int>()];
                    float[] times = ReadFloats(accessors, views, bin, sm["input"].Value<int>(), 1);
                    float[] quats = ReadFloats(accessors, views, bin, sm["output"].Value<int>(), 4);
                    Track tr = new Track(); tr.hb = bone; tracks[bone] = tr;
                    for (int k = 0; k < times.Length; k++)
                    {
                        Quaternion q = new Quaternion(quats[k * 4], quats[k * 4 + 1], quats[k * 4 + 2], quats[k * 4 + 3]);
                        tr.Add(times[k], Conv(q, VRMA_SIGN));
                        if (times[k] > maxT) maxT = times[k];
                    }
                }
            }
            PoseItem it = BuildItem(Path.GetFileNameWithoutExtension(path), tracks, new List<BlendTrack>(), maxT, anim);
            report = "VRMA: mapped " + tracks.Count + " humanoid bones -> " + it.keyframes.Count + " keyframes (" + maxT.ToString("0.0") + "s).";
            return it;
        }

        static void ParseGltf(byte[] bytes, out JObject gltf, out byte[] bin)
        {
            bin = null;
            if (bytes.Length > 12 && BitConverter.ToUInt32(bytes, 0) == 0x46546C67) // "glTF" (GLB)
            {
                int p = 12;
                string json = null;
                while (p + 8 <= bytes.Length)
                {
                    uint len = BitConverter.ToUInt32(bytes, p); uint type = BitConverter.ToUInt32(bytes, p + 4); p += 8;
                    if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(bytes, p, (int)len);       // JSON
                    else if (type == 0x004E4942) { bin = new byte[len]; Array.Copy(bytes, p, bin, 0, (int)len); } // BIN
                    p += (int)len;
                }
                gltf = JObject.Parse(json);
            }
            else { gltf = JObject.Parse(Encoding.UTF8.GetString(bytes)); } // .gltf text (external/base64 buffers unsupported)
        }

        static float[] ReadFloats(JArray accessors, JArray views, byte[] bin, int accessorIndex, int comps)
        {
            JObject acc = (JObject)accessors[accessorIndex];
            int count = acc["count"].Value<int>();
            int viewIdx = acc["bufferView"].Value<int>();
            int accOff = acc["byteOffset"] != null ? acc["byteOffset"].Value<int>() : 0;
            JObject bv = (JObject)views[viewIdx];
            int bvOff = bv["byteOffset"] != null ? bv["byteOffset"].Value<int>() : 0;
            int start = bvOff + accOff;
            float[] outv = new float[count * comps];
            for (int i = 0; i < count * comps; i++) outv[i] = BitConverter.ToSingle(bin, start + i * 4);
            return outv;
        }

        // ---------- helpers ----------
        static Encoding ShiftJIS()
        {
            try { return Encoding.GetEncoding("shift_jis"); } catch {}
            try { return Encoding.GetEncoding(932); } catch {}
            return null;
        }
        static string ReadFixedStr(byte[] d, int off, int len, Encoding enc)
        {
            int n = 0; while (n < len && d[off + n] != 0) n++;
            if (enc != null) { try { return enc.GetString(d, off, n); } catch {} }
            return Encoding.ASCII.GetString(d, off, n);
        }

        static bool BvhBone(string name, out HumanBodyBones hb)
        {
            string k = name.ToLowerInvariant();
            int c = k.IndexOf(':'); if (c >= 0) k = k.Substring(c + 1);        // strip "mixamorig:"
            k = k.Replace("_", "").Replace(" ", "").Replace(".", "");
            return BVH.TryGetValue(k, out hb);
        }
        static bool VrmHuman(string name, out HumanBodyBones hb)
        {
            // VRMA humanBones keys are lowerCamel: hips, spine, leftUpperArm, ...
            string k = char.ToUpperInvariant(name[0]) + name.Substring(1);
            return Enum.TryParse<HumanBodyBones>(k, out hb) && hb != HumanBodyBones.LastBone;
        }

        // MMD (shift-JIS) -> HumanBodyBones
        static readonly Dictionary<string, HumanBodyBones> MMD = new Dictionary<string, HumanBodyBones>()
        {
            {"センター",HumanBodyBones.Hips},{"下半身",HumanBodyBones.Hips},{"上半身",HumanBodyBones.Spine},
            {"上半身2",HumanBodyBones.Chest},{"首",HumanBodyBones.Neck},{"頭",HumanBodyBones.Head},
            {"左肩",HumanBodyBones.LeftShoulder},{"左腕",HumanBodyBones.LeftUpperArm},{"左ひじ",HumanBodyBones.LeftLowerArm},{"左手首",HumanBodyBones.LeftHand},
            {"右肩",HumanBodyBones.RightShoulder},{"右腕",HumanBodyBones.RightUpperArm},{"右ひじ",HumanBodyBones.RightLowerArm},{"右手首",HumanBodyBones.RightHand},
            {"左足",HumanBodyBones.LeftUpperLeg},{"左ひざ",HumanBodyBones.LeftLowerLeg},{"左足首",HumanBodyBones.LeftFoot},{"左つま先",HumanBodyBones.LeftToes},
            {"右足",HumanBodyBones.RightUpperLeg},{"右ひざ",HumanBodyBones.RightLowerLeg},{"右足首",HumanBodyBones.RightFoot},{"右つま先",HumanBodyBones.RightToes},
            {"左親指０",HumanBodyBones.LeftThumbProximal},{"左親指１",HumanBodyBones.LeftThumbIntermediate},{"左親指２",HumanBodyBones.LeftThumbDistal},
            {"左人指１",HumanBodyBones.LeftIndexProximal},{"左人指２",HumanBodyBones.LeftIndexIntermediate},{"左人指３",HumanBodyBones.LeftIndexDistal},
            {"左中指１",HumanBodyBones.LeftMiddleProximal},{"左中指２",HumanBodyBones.LeftMiddleIntermediate},{"左中指３",HumanBodyBones.LeftMiddleDistal},
            {"左薬指１",HumanBodyBones.LeftRingProximal},{"左薬指２",HumanBodyBones.LeftRingIntermediate},{"左薬指３",HumanBodyBones.LeftRingDistal},
            {"左小指１",HumanBodyBones.LeftLittleProximal},{"左小指２",HumanBodyBones.LeftLittleIntermediate},{"左小指３",HumanBodyBones.LeftLittleDistal},
            {"右親指０",HumanBodyBones.RightThumbProximal},{"右親指１",HumanBodyBones.RightThumbIntermediate},{"右親指２",HumanBodyBones.RightThumbDistal},
            {"右人指１",HumanBodyBones.RightIndexProximal},{"右人指２",HumanBodyBones.RightIndexIntermediate},{"右人指３",HumanBodyBones.RightIndexDistal},
            {"右中指１",HumanBodyBones.RightMiddleProximal},{"右中指２",HumanBodyBones.RightMiddleIntermediate},{"右中指３",HumanBodyBones.RightMiddleDistal},
            {"右薬指１",HumanBodyBones.RightRingProximal},{"右薬指２",HumanBodyBones.RightRingIntermediate},{"右薬指３",HumanBodyBones.RightRingDistal},
            {"右小指１",HumanBodyBones.RightLittleProximal},{"右小指２",HumanBodyBones.RightLittleIntermediate},{"右小指３",HumanBodyBones.RightLittleDistal},
        };

        // BVH / Mixamo joint aliases (lowercased, separators stripped) -> HumanBodyBones
        static readonly Dictionary<string, HumanBodyBones> BVH = new Dictionary<string, HumanBodyBones>()
        {
            {"hips",HumanBodyBones.Hips},{"hip",HumanBodyBones.Hips},{"pelvis",HumanBodyBones.Hips},
            {"spine",HumanBodyBones.Spine},{"spine1",HumanBodyBones.Chest},{"chest",HumanBodyBones.Chest},{"spine2",HumanBodyBones.UpperChest},{"neck",HumanBodyBones.Neck},{"head",HumanBodyBones.Head},
            {"leftshoulder",HumanBodyBones.LeftShoulder},{"lshoulder",HumanBodyBones.LeftShoulder},{"leftarm",HumanBodyBones.LeftUpperArm},{"leftupperarm",HumanBodyBones.LeftUpperArm},
            {"leftforearm",HumanBodyBones.LeftLowerArm},{"leftlowerarm",HumanBodyBones.LeftLowerArm},{"lefthand",HumanBodyBones.LeftHand},
            {"rightshoulder",HumanBodyBones.RightShoulder},{"rshoulder",HumanBodyBones.RightShoulder},{"rightarm",HumanBodyBones.RightUpperArm},{"rightupperarm",HumanBodyBones.RightUpperArm},
            {"rightforearm",HumanBodyBones.RightLowerArm},{"rightlowerarm",HumanBodyBones.RightLowerArm},{"righthand",HumanBodyBones.RightHand},
            {"leftupleg",HumanBodyBones.LeftUpperLeg},{"leftupperleg",HumanBodyBones.LeftUpperLeg},{"leftthigh",HumanBodyBones.LeftUpperLeg},
            {"leftleg",HumanBodyBones.LeftLowerLeg},{"leftlowerleg",HumanBodyBones.LeftLowerLeg},{"leftshin",HumanBodyBones.LeftLowerLeg},{"leftfoot",HumanBodyBones.LeftFoot},{"lefttoebase",HumanBodyBones.LeftToes},
            {"rightupleg",HumanBodyBones.RightUpperLeg},{"rightupperleg",HumanBodyBones.RightUpperLeg},{"rightthigh",HumanBodyBones.RightUpperLeg},
            {"rightleg",HumanBodyBones.RightLowerLeg},{"rightlowerleg",HumanBodyBones.RightLowerLeg},{"rightshin",HumanBodyBones.RightLowerLeg},{"rightfoot",HumanBodyBones.RightFoot},{"righttoebase",HumanBodyBones.RightToes},
        };
    }
}
