using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PoseStudio
{
    // Applies all PoseItems to a bound avatar every LateUpdate (after the Animator has
    // written its pose). Bone offsets are layered additively on top of the captured rest
    // pose; blendshape weights are added on top of their captured base. When an item fades
    // fully out, the affected bone/shape is released so the avatar's own animation resumes.
    public class PoseApplier
    {
        const float EPS = 0.0005f;

        // Per-item retarget and IK-pin details in Player.log (off: they repeat on every rebind).
        static readonly bool VERBOSE = false;

        // Per-item runtime envelope state.
        class ItemRT
        {
            public PoseItem item;
            public float env;     // eased 0..1 toward (active ? 1 : 0), or blendshape-driven
            public float phase;   // animation phase 0..1 (wave) or elapsed seconds (keyframes)
            public float amount;  // final applied strength this frame

            // Keyframe-timeline state (when item.useKeyframes): which two keyframes we are
            // currently between (kfA -> kfB) and how far through (kfU 0..1). Each bind reads
            // its own channel from these keyframes so every keyframe holds a distinct pose.
            public bool kfMode;
            public int kfA;
            public int kfB;
            public float kfU;
            public bool paused;   // timeline editor transport/scrub hold

            // Resolved blendshape-trigger source (when item.useTrigger).
            public SkinnedMeshRenderer trigR;
            public int trigIndex = -1;
        }

        // A transform (bone or mesh object) driven by one or more item targets.
        class BoneBind
        {
            public ItemRT rt;
            public ITransformTarget target;
            public string channelId; // keyframe-channel id for this target ("bone:"/"mesh:")

            // Retarget, filled in when the item carries a rigRef (see RetargetBone).
            public bool retarget;
            public Quaternion rq = Quaternion.identity; // re-expresses the offset in THIS rig's axes
            public Vector3 posScale = Vector3.one;      // proportions of this body vs the authored one
            public float strength = 1f;

            // The user's own per-group fit-up for this bone (see GroupAdjust).
            public Vector3 fitScale = Vector3.one;
            public Vector3 fitPos = Vector3.zero;
            public Quaternion fitRot = Quaternion.identity;                 // per-animation rotation strength
        }

        class BoneGroup
        {
            public Transform t;
            public bool engaged;
            public Vector3 basePos;
            public Quaternion baseRot;
            public Vector3 baseScale;
            public Vector3 lastScale = Vector3.one; // final scale this frame (for the late re-apply)
            public bool usesScale;                  // any active bind drives scale
            public readonly List<BoneBind> binds = new List<BoneBind>();
        }

        class BlendBind
        {
            public ItemRT rt;
            public BlendTarget target;
            public string channelId; // keyframe-channel id for this shape ("blend:Mesh::Shape")
        }

        // One VRM blendshape clip driven by one or more items. Its summed value is handed to
        // VNyan's expression pass (see PoseStudioPlugin's blendshape listener), ADDED on top of
        // whatever tracking set that frame, the same way mesh blendshapes are added.
        class ClipGroup
        {
            public string clip;
            public readonly List<BlendBind> binds = new List<BlendBind>();
            // The clip's own mesh shapes, for the part VNyan will not take (below zero).
            public readonly List<ClipMesh> meshes = new List<ClipMesh>();
            public float neg;            // this frame's below-zero amount, in percent
        }

        // One mesh blendshape a VRM clip drives, and how hard (0..100 at clip value 1).
        class ClipMesh
        {
            public SkinnedMeshRenderer r;
            public int index;
            public float w;
            public bool engaged;
            public float baseW;          // what the shape was before we pushed it
            public float lastW;          // what we last wrote, to notice anyone else writing
            public bool logged;
        }

        // Read a VRM clip's mesh bindings off the avatar's VRMBlendShapeProxy. Reflection, so
        // UniVRM is not a build dependency of the plugin.
        void ResolveClipMeshes(ClipGroup cg)
        {
            cg.meshes.Clear();
            if (avatar == null) return;
            try
            {
                Component[] comps = avatar.GetComponentsInChildren<Component>(true);
                for (int c = 0; c < comps.Length; c++)
                {
                    if (comps[c] == null || comps[c].GetType().Name != "VRMBlendShapeProxy") continue;
                    Transform root = comps[c].transform;
                    object asset = Member(comps[c], "BlendShapeAvatar");
                    System.Collections.IEnumerable clips = Member(asset, "Clips") as System.Collections.IEnumerable;
                    if (clips == null) continue;
                    foreach (object clip in clips)
                    {
                        if ((Member(clip, "BlendShapeName") as string) != cg.clip) continue;
                        System.Collections.IEnumerable vals = Member(clip, "Values") as System.Collections.IEnumerable;
                        if (vals == null) continue;
                        foreach (object b in vals)
                        {
                            string path = Member(b, "RelativePath") as string;
                            object io = Member(b, "Index"), wo = Member(b, "Weight");
                            if (path == null || io == null || wo == null) continue;
                            Transform t = root.Find(path);
                            if (t == null) t = avatar.transform.Find(path);
                            SkinnedMeshRenderer r = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                            if (r == null) continue;
                            ClipMesh m = new ClipMesh();
                            m.r = r;
                            m.index = System.Convert.ToInt32(io);
                            m.w = System.Convert.ToSingle(wo);
                            cg.meshes.Add(m);
                        }
                    }
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[PoseStudio] reading clip '" + cg.clip + "': " + e.Message); }
        }

        static object Member(object o, string name)
        {
            if (o == null) return null;
            System.Type t = o.GetType();
            System.Reflection.PropertyInfo p = t.GetProperty(name);
            if (p != null) return p.GetValue(o, null);
            System.Reflection.FieldInfo f = t.GetField(name);
            return f != null ? f.GetValue(o) : null;
        }

        // Push the below-zero part of each clip onto its mesh shapes. Run late in the frame and
        // every frame: VNyan rewrites clip shapes itself, so whatever it wrote is the new base.
        public void ApplyClipNegatives()
        {
            for (int ci = 0; ci < clipGroups.Count; ci++)
            {
                ClipGroup cg = clipGroups[ci];
                for (int m = 0; m < cg.meshes.Count; m++)
                {
                    ClipMesh cm = cg.meshes[m];
                    if (cm.r == null) continue;
                    float cur = cm.r.GetBlendShapeWeight(cm.index);
                    if (cg.neg < -0.0001f)
                    {
                        if (!cm.engaged || Mathf.Abs(cur - cm.lastW) > 0.001f) cm.baseW = cur;
                        cm.engaged = true;
                        float v = Mathf.Clamp(cm.baseW + cg.neg * cm.w / 100f, -BLEND_LIMIT, BLEND_LIMIT);
                        cm.r.SetBlendShapeWeight(cm.index, v);
                        cm.lastW = v;
                        if (!cm.logged)
                        {
                            cm.logged = true;
                            Debug.Log("[PoseStudio] VRM clip '" + cg.clip + "' below zero: '" + cm.r.name + "' shape "
                                      + cm.index + " " + cm.baseW.ToString("0.0") + " -> " + v.ToString("0.0"));
                        }
                    }
                    else if (cm.engaged)
                    {
                        if (Mathf.Abs(cur - cm.lastW) <= 0.001f) cm.r.SetBlendShapeWeight(cm.index, cm.baseW);
                        cm.engaged = false;
                    }
                }
            }
        }

        // clip name -> extra amount (0..1) to add this frame. Read by the plugin's listener.
        public readonly Dictionary<string, float> clipValues = new Dictionary<string, float>();
        readonly List<ClipGroup> clipGroups = new List<ClipGroup>();

        // How far a blendshape (or VRM clip, as a fraction) may be pushed either way: 1000%.
        public const float BLEND_LIMIT = 1000f;

        class BlendGroup
        {
            public SkinnedMeshRenderer r;
            public int index;
            public bool engaged;
            public float baseWeight;
            public readonly List<BlendBind> binds = new List<BlendBind>();
            // Worked out in Apply (Update), written in ApplyLateBlends (after every LateUpdate).
            public bool want;
            public float sum;
            public float lastW;          // what we last wrote, to notice anyone else writing
        }

        // One resolved 2-bone IK goal. Solved after FK each frame while its item is active.
        class IKBind
        {
            public bool retarget;
            public Quaternion rq = Quaternion.identity;
            public Vector3 posScale = Vector3.one;
            public Vector3 fitPos = Vector3.zero;
            public Vector3 fitScale = Vector3.one;
            public ItemRT rt;
            public IKGoal goal;
            public Transform up;     // upper bone (thigh / upper arm)
            public Transform mid;    // lower bone (shin / forearm)
            public Transform end;    // end effector (foot / wrist)
            public Transform space;  // reference frame for the target (null = world)
            public bool captured;
            public Vector3 tgtPos;       // target position in `space` (local to space)
            public Quaternion tgtRot;    // target rotation in `space` (local to space)
            public string channelId;     // "ik:<name>" animated-offset channel id
            public bool animated;        // true if a keyframe carries an ik: offset channel
            public bool engaged;
            public Quaternion baseUp, baseMid, baseEnd; // captured local rots for restore
        }

        GameObject avatar;
        Animator animator;
        PoseConfig config;

        // A renderer to disable while any of its `items` are active (restored otherwise).
        class HideGroup
        {
            public Renderer r;
            public bool baseEnabled;
            public bool hidden;
            public readonly List<ItemRT> items = new List<ItemRT>();
        }

        readonly List<ItemRT> items = new List<ItemRT>();
        readonly List<BoneGroup> boneGroups = new List<BoneGroup>();
        readonly List<BlendGroup> blendGroups = new List<BlendGroup>();
        readonly List<IKBind> ikBinds = new List<IKBind>();
        readonly List<HideGroup> hideGroups = new List<HideGroup>();

        // ---- Retargeting ------------------------------------------------------------------
        // An authored offset is a rotation in the AUTHORING rig's bone axes. Written straight
        // onto another rig whose bones rest at different angles it means something else, so it
        // is re-expressed: the offset is read as a world-space turn relative to the authored
        // rest pose and then written back in this bone's own rest frame.
        //     q  = inverse(thisRestWorld) * authoredRestWorld
        //     offsetHere = q * offsetAuthored * inverse(q)
        // Positions (sway, crouch, stance width, hand goals) are plain metres, so they are
        // scaled by how this body compares with the one the animation was made on.
        static Quaternion RigRot(RigRef r, string bone)
        {
            float[] q;
            if (r == null || r.restRot == null || !r.restRot.TryGetValue(bone, out q) || q == null || q.Length != 4)
                return Quaternion.identity;
            return new Quaternion(q[0], q[1], q[2], q[3]);
        }

        static bool HasRigRot(RigRef r, string bone)
        {
            return r != null && r.restRot != null && r.restRot.ContainsKey(bone);
        }

        float BoneSpan(string a, string b)
        {
            Transform ta = PoseUtil.Find(avatar.transform, animator, a);
            Transform tb = PoseUtil.Find(avatar.transform, animator, b);
            if (ta == null || tb == null) return 0f;
            Vector3 pa, pb;
            if (!restPos.TryGetValue(ta.name, out pa)) pa = ta.position;
            if (!restPos.TryGetValue(tb.name, out pb)) pb = tb.position;
            return Vector3.Distance(pa, pb);
        }

        // How this avatar compares with the one the animation was made on. Sideways is scaled
        // by WIDTH and up/down by LENGTH, separately: a much wider model needs its feet further
        // apart and its hands further out, but not taller steps or a deeper crouch.
        void MeasureRig(PoseItem it, out Vector3 kBody, out Vector3 kArm,
                        out float pushBody, out float pushArm)
        {
            kBody = Vector3.one; kArm = Vector3.one;
            pushBody = 0f; pushArm = 0f;
            RigRef r = it.rigRef;
            if (r == null) return;

            float leg = BoneSpan("LeftUpperLeg", "LeftLowerLeg") + BoneSpan("LeftLowerLeg", "LeftFoot");
            if (leg <= 0f) leg = BoneSpan("RightUpperLeg", "RightLowerLeg") + BoneSpan("RightLowerLeg", "RightFoot");
            float arm = BoneSpan("LeftUpperArm", "LeftLowerArm") + BoneSpan("LeftLowerArm", "LeftHand");
            if (arm <= 0f) arm = BoneSpan("RightUpperArm", "RightLowerArm") + BoneSpan("RightLowerArm", "RightHand");
            float ankles = BoneSpan("LeftFoot", "RightFoot");
            float shoulders = BoneSpan("LeftUpperArm", "RightUpperArm");

            float kLegLen = Ratio(leg, r.legLength);
            float kArmLen = Ratio(arm, r.armLength);
            if (kLegLen == 1f && r.hipHeight > 0.001f)
            {
                Transform hips = PoseUtil.Find(avatar.transform, animator, "Hips");
                if (hips != null) kLegLen = Ratio(hips.position.y - avatar.transform.position.y, r.hipHeight);
            }
            float kStance = Ratio(ankles, r.ankleGap);
            float kSpan = Ratio(shoulders, r.shoulderGap);
            if (kStance == 1f) kStance = kLegLen;      // older files have no widths recorded
            if (kSpan == 1f) kSpan = kArmLen;

            kBody = new Vector3(kStance, kLegLen, kLegLen);
            kArm = new Vector3(kSpan, kArmLen, kArmLen);

            // whatever the user dialled in for this animation, on top. A per-rig entry wins
            // over the general one, so each model can be tuned without disturbing the others.
            string sig = RigSignature(leg, arm, ankles, shoulders);
            rigSignature = sig;
            PoseAdjust adj = it.adjust;
            if (it.adjustPerRig != null)
            {
                PoseAdjust per;
                if (it.adjustPerRig.TryGetValue(sig, out per) && per != null) adj = per;
            }
            if (VERBOSE)
                Debug.Log("[PoseStudio] '" + it.name + "' on rig " + sig
                          + (adj != null ? " (tuned)" : " (no tuning)"));
            if (adj != null)
            {
                // The hand tuning is deliberately NOT clamped: a body far from the authored one
                // can need several times the movement, and the IK stops it going anywhere the
                // arm cannot reach anyway.
                kBody = Vector3.Scale(kBody, new Vector3(adj.bodyWidth, adj.bodyHeight, adj.bodyDepth));
                kArm = Vector3.Scale(kArm, new Vector3(adj.armReach, adj.armHeight, adj.armHeight));
                // Scaling alone cannot move a limb that starts near her middle: ten times a tiny
                // offset is still tiny. So the same numbers ALSO shove the goal straight out
                // sideways, by a share of the limb's own size.
                pushArm = (adj.armReach - 1f) * (arm > 0.001f ? arm : 0.45f) * 0.5f;
                pushBody = (adj.bodyWidth - 1f) * (ankles > 0.001f ? ankles : 0.15f) * 0.5f;
            }
        }

        // The user's own fit-up for the group a bone belongs to. Sideways is mirrored on the
        // right so a slider spreads a pair apart rather than sliding both the same way.
        void FitFor(PoseItem it, string bone, out Vector3 scale, out Vector3 pos, out Quaternion rot)
        {
            scale = Vector3.one; pos = Vector3.zero; rot = Quaternion.identity;
            if (it.fit == null || it.fit.Count == 0 || string.IsNullOrEmpty(bone)) return;
            string grp = GroupAdjust.GroupOf(bone);
            // Which way is OUT for this limb, read from where it rests rather than from its name:
            // a positive x fit then always spreads a pair apart, on any rig, whichever side its
            // "Left" happens to sit on.
            Vector3 rp;
            bool flip = restPos.TryGetValue(bone, out rp) ? rp.x < 0f : bone.Contains("Left");
            bool right = flip;
            for (int i = 0; i < it.fit.Count; i++)
            {
                GroupAdjust f = it.fit[i];
                if (f == null) continue;
                if (f.group != grp && f.group != "All") continue;
                Vector3 fs = PoseUtil.ToVector3(f.scale, Vector3.one);
                Vector3 fp = PoseUtil.ToVector3(f.position, Vector3.zero);
                Vector3 fr = PoseUtil.ToVector3(f.rotation, Vector3.zero);
                if (right) { fp.x = -fp.x; fr.y = -fr.y; fr.z = -fr.z; }
                scale = Vector3.Scale(scale, fs);
                pos += fp;
                rot = Quaternion.Euler(fr) * rot;
            }
        }

        // REST POSE CACHE. Bind() can run while an animation is already playing (a config
        // reload rebinds on the spot), and then every "rest" reading is really the posed
        // skeleton: the measurements feed back on themselves and the bone axes come out wrong.
        // So the rest pose is captured once per avatar, on the first bind after it loads, and
        // reused from then on.
        GameObject restOwner;
        readonly Dictionary<string, Quaternion> restRot = new Dictionary<string, Quaternion>();
        readonly Dictionary<string, Vector3> restPos = new Dictionary<string, Vector3>();

        void CaptureRest()
        {
            if (restOwner == avatar) return;      // already have this avatar's rest pose
            restOwner = avatar;
            restRot.Clear();
            restPos.Clear();
            if (avatar == null) return;
            Transform[] all = avatar.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                restRot[all[i].name] = all[i].rotation;
                restPos[all[i].name] = all[i].position;
            }
        }

        Quaternion RestRotOf(Transform tr)
        {
            Quaternion q;
            if (tr != null && restRot.TryGetValue(tr.name, out q)) return q;
            return tr != null ? tr.rotation : Quaternion.identity;
        }

        // A short, stable name for a body: limb lengths and widths in centimetres. Two different
        // models almost never match, and the same model always does.
        static string RigSignature(float leg, float arm, float ankles, float shoulders)
        {
            return "L" + Mathf.RoundToInt(leg * 100f) + "-A" + Mathf.RoundToInt(arm * 100f)
                 + "-S" + Mathf.RoundToInt(ankles * 100f) + "-W" + Mathf.RoundToInt(shoulders * 100f);
        }

        static float Ratio(float mine, float authored)
        {
            if (authored <= 0.001f || mine <= 0.001f) return 1f;
            return Mathf.Clamp(mine / authored, 0.2f, 5f);   // automatic part stays sane
        }

        // The bound body's signature, so the window can store tuning per model.
        public string rigSignature = "";
        float worstTwist;          // how far the rotation retarget moves this rig's bones
        string worstTwistBone = "";

        public void Bind(GameObject av, Animator anim, PoseConfig cfg)
        {
            Restore();
            avatar = av;
            CaptureRest();     // before anything else: the rest pose is the reference for all of it
            animator = anim;
            config = cfg;

            items.Clear();
            boneGroups.Clear();
            blendGroups.Clear();
            clipGroups.Clear();
            clipValues.Clear();
            Dictionary<string, ClipGroup> clipMap = new Dictionary<string, ClipGroup>();
            ikBinds.Clear();
            hideGroups.Clear();

            if (avatar == null) return;
            rigSignature = RigSignature(
                BoneSpan("LeftUpperLeg", "LeftLowerLeg") + BoneSpan("LeftLowerLeg", "LeftFoot"),
                BoneSpan("LeftUpperArm", "LeftLowerArm") + BoneSpan("LeftLowerArm", "LeftHand"),
                BoneSpan("LeftFoot", "RightFoot"), BoneSpan("LeftUpperArm", "RightUpperArm"));
            if (config == null || config.items == null) return;

            Dictionary<Transform, BoneGroup> boneMap = new Dictionary<Transform, BoneGroup>();
            Dictionary<string, BlendGroup> blendMap = new Dictionary<string, BlendGroup>();

            for (int i = 0; i < config.items.Count; i++)
            {
                PoseItem it = config.items[i];
                if (it == null) continue;

                ItemRT rt = new ItemRT();
                rt.item = it;
                rt.env = it.active ? 1f : 0f; // start settled so loaded toggles don't pop
                rt.phase = 0f;

                // Resolve the blendshape-trigger source (a shape we READ, not write).
                rt.trigR = null;
                rt.trigIndex = -1;
                if (it.useTrigger && !string.IsNullOrEmpty(it.triggerShape))
                {
                    SkinnedMeshRenderer tr = PoseUtil.FindRenderer(avatar, it.triggerMesh, it.triggerShape);
                    if (tr != null && tr.sharedMesh != null)
                    {
                        int ti = tr.sharedMesh.GetBlendShapeIndex(it.triggerShape);
                        if (ti >= 0) { rt.trigR = tr; rt.trigIndex = ti; }
                        else Debug.LogWarning("[PoseStudio] trigger blendshape '" + it.triggerShape + "' missing (item '" + it.name + "')");
                    }
                    else Debug.LogWarning("[PoseStudio] trigger source for '" + it.name + "' not found");
                }

                items.Add(rt);

                // How this body compares with the one the item was authored on.
                bool doRetarget = it.retarget && (it.rigRef != null || it.adjust != null);
                Vector3 kBody = Vector3.one, kArm = Vector3.one;
                float pushBody = 0f, pushArm = 0f;
                worstTwist = 0f; worstTwistBone = "";
                if (doRetarget)
                {
                    MeasureRig(it, out kBody, out kArm, out pushBody, out pushArm);
                    if (VERBOSE) Debug.Log("[PoseStudio] '" + it.name + "' retargeted to this avatar: stance x"
                              + kBody.x.ToString("0.000") + ", legs x" + kBody.y.ToString("0.000")
                              + ", reach x" + kArm.x.ToString("0.000")
                              + ", arms x" + kArm.y.ToString("0.000"));
                }

                // Bone targets.
                if (it.bones != null)
                    for (int b = 0; b < it.bones.Count; b++)
                    {
                        BoneTarget bt = it.bones[b];
                        if (bt == null || string.IsNullOrEmpty(bt.bone)) continue;
                        Transform tr = PoseUtil.Find(avatar.transform, animator, bt.bone);
                        if (tr == null)
                        {
                            Debug.LogWarning("[PoseStudio] bone '" + bt.bone + "' not found (item '" + it.name + "')");
                            continue;
                        }
                        BoneGroup g;
                        if (!boneMap.TryGetValue(tr, out g))
                        {
                            g = new BoneGroup();
                            g.t = tr;
                            boneMap[tr] = g;
                            boneGroups.Add(g);
                        }
                        BoneBind bind = new BoneBind();
                        bind.rt = rt;
                        bind.target = bt;
                        bind.channelId = KeyChannels.BoneId(bt.bone);
                        if (doRetarget)
                        {
                            bind.retarget = true;
                            if (HasRigRot(it.rigRef, bt.bone))
                                bind.rq = Quaternion.Inverse(RestRotOf(tr)) * RigRot(it.rigRef, bt.bone);
                            bind.posScale = (GroupAdjust.GroupOf(bt.bone) == "Arms"
                                             || GroupAdjust.GroupOf(bt.bone) == "Hands") ? kArm : kBody;
                            bind.strength = it.adjust != null ? it.adjust.strength : 1f;
                        }
                        // the user's own per-group fit-up, which applies with or without a rigRef
                        FitFor(it, bt.bone, out bind.fitScale, out bind.fitPos, out bind.fitRot);
                        g.binds.Add(bind);
                    }

                if (doRetarget && worstTwist > 0.5f)
                    Debug.Log("[PoseStudio] '" + it.name + "' rest pose differs most at "
                              + worstTwistBone + " by " + worstTwist.ToString("0.0") + " deg");

                // Mesh-object transform targets (share the same BoneGroup machinery, a
                // mesh object is just another Transform we offset over its rest pose).
                if (it.meshes != null)
                    for (int m = 0; m < it.meshes.Count; m++)
                    {
                        MeshTarget mt = it.meshes[m];
                        if (mt == null || string.IsNullOrEmpty(mt.mesh)) continue;
                        Transform tr = PoseUtil.FindMeshTransform(avatar, mt.mesh);
                        if (tr == null)
                        {
                            Debug.LogWarning("[PoseStudio] mesh '" + mt.mesh + "' not found (item '" + it.name + "')");
                            continue;
                        }
                        BoneGroup g;
                        if (!boneMap.TryGetValue(tr, out g))
                        {
                            g = new BoneGroup();
                            g.t = tr;
                            boneMap[tr] = g;
                            boneGroups.Add(g);
                        }
                        BoneBind bind = new BoneBind();
                        bind.rt = rt;
                        bind.target = mt;
                        bind.channelId = KeyChannels.MeshId(mt.mesh);
                        g.binds.Add(bind);
                    }

                // Blendshape targets.
                if (it.blendshapes != null)
                    for (int s = 0; s < it.blendshapes.Count; s++)
                    {
                        BlendTarget bt = it.blendshapes[s];
                        if (bt == null || string.IsNullOrEmpty(bt.shape)) continue;
                        if (bt.IsClip)
                        {
                            ClipGroup cg;
                            if (!clipMap.TryGetValue(bt.shape, out cg))
                            {
                                cg = new ClipGroup();
                                cg.clip = bt.shape;
                                clipMap[bt.shape] = cg;
                                clipGroups.Add(cg);
                                ResolveClipMeshes(cg);
                            }
                            BlendBind cb = new BlendBind();
                            cb.rt = rt;
                            cb.target = bt;
                            cb.channelId = KeyChannels.BlendId(bt.mesh, bt.shape);
                            cg.binds.Add(cb);
                            continue;
                        }
                        SkinnedMeshRenderer r = PoseUtil.FindRenderer(avatar, bt.mesh, bt.shape);
                        if (r == null || r.sharedMesh == null)
                        {
                            Debug.LogWarning("[PoseStudio] blendshape '" + bt.shape + "' mesh not found (item '" + it.name + "')");
                            continue;
                        }
                        int idx = r.sharedMesh.GetBlendShapeIndex(bt.shape);
                        if (idx < 0)
                        {
                            Debug.LogWarning("[PoseStudio] blendshape '" + bt.shape + "' missing on '" + r.name + "'");
                            continue;
                        }
                        string key = r.GetInstanceID() + "#" + idx;
                        BlendGroup g;
                        if (!blendMap.TryGetValue(key, out g))
                        {
                            g = new BlendGroup();
                            g.r = r;
                            g.index = idx;
                            blendMap[key] = g;
                            blendGroups.Add(g);
                        }
                        BlendBind bind = new BlendBind();
                        bind.rt = rt;
                        bind.target = bt;
                        bind.channelId = KeyChannels.BlendId(bt.mesh, bt.shape);
                        g.binds.Add(bind);
                    }

                // IK goals (2-bone foot/hand pins).
                if (it.ikGoals != null)
                    for (int k = 0; k < it.ikGoals.Count; k++)
                    {
                        IKGoal goal = it.ikGoals[k];
                        if (goal == null || !goal.enabled) continue;
                        if (string.IsNullOrEmpty(goal.upper) || string.IsNullOrEmpty(goal.lower) || string.IsNullOrEmpty(goal.end))
                            continue;
                        Transform up = PoseUtil.Find(avatar.transform, animator, goal.upper);
                        Transform mid = PoseUtil.Find(avatar.transform, animator, goal.lower);
                        Transform end = PoseUtil.Find(avatar.transform, animator, goal.end);
                        if (up == null || mid == null || end == null)
                        {
                            Debug.LogWarning("[PoseStudio] IK goal '" + goal.name + "' bone missing (item '" + it.name + "')");
                            continue;
                        }
                        Transform sp;
                        if (goal.space == "world") sp = null;
                        else if (goal.space == "root" || string.IsNullOrEmpty(goal.space)) sp = avatar.transform;
                        else
                        {
                            sp = PoseUtil.Find(avatar.transform, animator, goal.space);
                            if (sp == null) sp = avatar.transform; // fall back to root
                        }
                        bool handGoal = (goal.end != null && (goal.end.Contains("Hand") || goal.end.Contains("wrist")))
                                        || (goal.name != null && goal.name.Contains("Hand"));
                        Vector3 fitS, fitP; Quaternion fitR;
                        FitFor(it, string.IsNullOrEmpty(goal.end) ? goal.name : goal.end,
                               out fitS, out fitP, out fitR);
                        IKBind ib = new IKBind();
                        // A fit offset is written in BODY terms (x out to the sides, y up,
                        // z forward). Each goal is pinned in its own space though: feet in root
                        // space, hands in chest space, which points differently on every rig.
                        // Convert, or "push the hands forward" pushes them backwards on a model
                        // whose chest bone happens to face the other way.
                        if (sp != null)
                            fitP = Quaternion.Inverse(RestRotOf(sp)) * (avatar.transform.rotation * fitP);
                        ib.fitScale = fitS; ib.fitPos = fitP;
                        ib.rt = rt;
                        ib.goal = goal;
                        ib.up = up; ib.mid = mid; ib.end = end; ib.space = sp;
                        if (!goal.capture)
                        {
                            ib.captured = true;
                            // An explicit target is authored metres, so it scales with the body
                            ib.tgtPos = PoseUtil.ToVector3(goal.position, Vector3.zero);
                            if (doRetarget)
                                ib.tgtPos = Vector3.Scale(ib.tgtPos, handGoal ? kArm : kBody);
                            ib.tgtPos = Vector3.Scale(ib.tgtPos, fitS) + fitP;
                            ib.tgtRot = Quaternion.Euler(PoseUtil.ToVector3(goal.rotation, Vector3.zero));
                        }
                        // Animated target: if any keyframe carries an "ik:<name>" channel, this
                        // goal's pin is offset per-keyframe (added to the captured base target).
                        if (doRetarget)
                        {
                            ib.retarget = true;
                            ib.posScale = handGoal ? kArm : kBody;
                            if (HasRigRot(it.rigRef, goal.end) && end != null)
                                ib.rq = Quaternion.Inverse(RestRotOf(end)) * RigRot(it.rigRef, goal.end);
                        }
                        if (doRetarget && VERBOSE)
                            Debug.Log("[PoseStudio]   goal " + goal.name + " scale " + ib.posScale
                                      + " target " + ib.tgtPos + " captured " + ib.captured);
                        ib.channelId = KeyChannels.IkId(goal.name);
                        ib.animated = false;
                        if (it.useKeyframes && it.keyframes != null)
                            for (int kk = 0; kk < it.keyframes.Count; kk++)
                                if (KeyChannels.Find(it.keyframes[kk], ib.channelId) != null)
                                { ib.animated = true; break; }
                        ikBinds.Add(ib);
                    }
            }

            // Bind-time capture: goals with captureMode "bind" pin to the avatar's current
            // REST/neutral pose (e.g. feet, so they sit CENTERED under the body) rather than
            // to the first play frame (which is mid-sway and would pin them off to one side).
            for (int i = 0; i < ikBinds.Count; i++)
            {
                IKBind ib = ikBinds[i];
                if (ib.captured) continue; // explicit target already provided
                if (ib.goal == null || ib.goal.captureMode != "bind") continue;
                if (ib.end == null) continue;
                Vector3 endRestPos;
                if (!restPos.TryGetValue(ib.end.name, out endRestPos)) endRestPos = ib.end.position;
                Quaternion endRestRot = RestRotOf(ib.end);
                if (ib.space != null)
                {
                    ib.tgtPos = ib.space.InverseTransformPoint(endRestPos);
                    ib.tgtRot = Quaternion.Inverse(ib.space.rotation) * endRestRot;
                }
                else { ib.tgtPos = endRestPos; ib.tgtRot = endRestRot; }
                // The capture is this avatar's own rest pose, so it needs no scaling, but the
                // user's fit-up has to land on TOP of it: without this a "spread the feet"
                // adjustment was thrown away the moment the goal captured.
                ib.tgtPos += ib.fitPos;
                ib.captured = true;
                if (VERBOSE) Debug.Log("[PoseStudio]   pinned " + ib.goal.name + " at " + ib.tgtPos
                          + " (fit " + ib.fitPos + ")");
            }

            // Hide-mesh groups: renderers to disable while an item is active.
            Renderer[] allRends = avatar.GetComponentsInChildren<Renderer>(true);
            Dictionary<Renderer, HideGroup> hideMap = new Dictionary<Renderer, HideGroup>();
            for (int ii = 0; ii < items.Count; ii++)
            {
                ItemRT rt = items[ii];
                if (rt.item == null || rt.item.hideMeshes == null) continue;
                for (int h = 0; h < rt.item.hideMeshes.Count; h++)
                {
                    string nm = rt.item.hideMeshes[h];
                    if (string.IsNullOrEmpty(nm)) continue;
                    string low = nm.ToLowerInvariant();
                    for (int r = 0; r < allRends.Length; r++)
                    {
                        Renderer rend = allRends[r];
                        if (rend == null || rend.name.ToLowerInvariant() != low) continue;
                        HideGroup hg;
                        if (!hideMap.TryGetValue(rend, out hg))
                        {
                            hg = new HideGroup(); hg.r = rend;
                            hideMap[rend] = hg; hideGroups.Add(hg);
                        }
                        if (!hg.items.Contains(rt)) hg.items.Add(rt);
                    }
                }
            }

            // One-time: dump the avatar's REST skeleton + blendshape names so an external
            // retargeter can compute axis-agnostic bone rotations (no per-rig axis guessing).
            try { DumpSkeleton(); } catch (System.Exception e) { Debug.LogWarning("[PoseStudio] skeleton dump failed: " + e.Message); }
        }

        // Writes rest-pose world/local rotations for every humanoid bone (incl. fingers/toes)
        // and every mesh's blendshape names to <persistentDataPath>/posestudio_skeleton.json.
        // Called from Bind() while the avatar is still in its neutral/rest pose.
        void DumpSkeleton()
        {
            if (avatar == null || animator == null || !animator.isHuman) return;
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");

            // root reference frame
            Transform root = avatar.transform;
            sb.Append("\"root\":");
            AppendXform(sb, ci, root, null);
            sb.Append(",\n\"bones\":{\n");

            bool first = true;
            int last = (int)HumanBodyBones.LastBone;
            for (int b = 0; b < last; b++)
            {
                HumanBodyBones hb = (HumanBodyBones)b;
                Transform t = animator.GetBoneTransform(hb);
                if (t == null) continue;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("\"").Append(hb.ToString()).Append("\":");
                AppendXform(sb, ci, t, t.parent);
            }
            sb.Append("\n},\n\"blendshapes\":{\n");

            SkinnedMeshRenderer[] rends = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            bool rfirst = true;
            for (int r = 0; r < rends.Length; r++)
            {
                SkinnedMeshRenderer smr = rends[r];
                if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                if (!rfirst) sb.Append(",\n");
                rfirst = false;
                sb.Append("\"").Append(Esc(smr.name)).Append("\":[");
                int bc = smr.sharedMesh.blendShapeCount;
                for (int s = 0; s < bc; s++)
                {
                    if (s > 0) sb.Append(",");
                    sb.Append("\"").Append(Esc(smr.sharedMesh.GetBlendShapeName(s))).Append("\"");
                }
                sb.Append("]");
            }
            sb.Append("\n}\n}\n");

            string path = Path.Combine(Application.persistentDataPath, "posestudio_skeleton.json");
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[PoseStudio] wrote skeleton dump -> " + path);
        }

        static void AppendXform(StringBuilder sb, CultureInfo ci, Transform t, Transform parent)
        {
            Vector3 wp = t.position; Quaternion wr = t.rotation; Quaternion lr = t.localRotation;
            Quaternion pr = parent != null ? parent.rotation : Quaternion.identity;
            sb.Append("{\"name\":\"").Append(Esc(t.name)).Append("\"");
            sb.Append(",\"wpos\":[").Append(F(wp.x,ci)).Append(",").Append(F(wp.y,ci)).Append(",").Append(F(wp.z,ci)).Append("]");
            sb.Append(",\"wrot\":[").Append(F(wr.x,ci)).Append(",").Append(F(wr.y,ci)).Append(",").Append(F(wr.z,ci)).Append(",").Append(F(wr.w,ci)).Append("]");
            sb.Append(",\"lrot\":[").Append(F(lr.x,ci)).Append(",").Append(F(lr.y,ci)).Append(",").Append(F(lr.z,ci)).Append(",").Append(F(lr.w,ci)).Append("]");
            sb.Append(",\"prot\":[").Append(F(pr.x,ci)).Append(",").Append(F(pr.y,ci)).Append(",").Append(F(pr.z,ci)).Append(",").Append(F(pr.w,ci)).Append("]");
            sb.Append("}");
        }

        static string F(float v, CultureInfo ci) { return v.ToString("R", ci); }
        static string Esc(string s) { return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

        static float Wave(string form, float phase)
        {
            // Returns 0..1, starting and ending at 0 so animations fade through rest.
            if (form == "triangle")
                return phase < 0.5f ? phase * 2f : (1f - phase) * 2f;
            if (form == "pulse")
                return phase < 0.5f ? 1f : 0f;
            // sine (default): smooth 0 -> 1 -> 0
            return 0.5f - 0.5f * Mathf.Cos(phase * 2f * Mathf.PI);
        }

        // Total loop length: sum of every keyframe's seconds-to-next (the last keyframe's
        // seconds loops back to the first).
        static float KeyCycle(List<PoseKeyframe> ks)
        {
            float c = 0f;
            for (int i = 0; i < ks.Count; i++)
                if (ks[i] != null) c += Mathf.Max(0f, ks[i].seconds);
            return c;
        }

        // Resolve which two keyframes elapsed time t (wrapped to [0,cycle)) falls between,
        // and how far through (u 0..1). Segment k runs from ks[k] to ks[(k+1)%n] over
        // ks[k].seconds. With a single keyframe (or a zero cycle) it parks on keyframe 0.
        static void ComputeSegment(List<PoseKeyframe> ks, float t, float cycle, out int a, out int b, out float u)
        {
            int n = ks.Count;
            a = 0; b = 0; u = 0f;
            if (n <= 0) return;
            if (n == 1 || cycle <= 0.0001f) return;

            float acc = 0f;
            for (int k = 0; k < n; k++)
            {
                int next = (k + 1) % n;
                float d = Mathf.Max(0f, ks[k] != null ? ks[k].seconds : 0f);
                if (d <= 0.0001f) continue; // instantaneous segment, skip
                if (t < acc + d || k == n - 1)
                {
                    a = k; b = next; u = Mathf.Clamp01((t - acc) / d);
                    return;
                }
                acc += d;
            }
            a = 0; b = (n > 1) ? 1 : 0; u = 0f;
        }

        static KeyframeChannel ChannelAt(List<PoseKeyframe> ks, int idx, string id)
        {
            if (ks == null || idx < 0 || idx >= ks.Count) return null;
            return KeyChannels.Find(ks[idx], id);
        }

        // Interpolate a transform channel's offset between keyframes a and b. Missing
        // channels read as identity (zero pos/rot, unit scale) so a target absent from a
        // keyframe simply contributes nothing there.
        static void SampleTransformChannel(List<PoseKeyframe> ks, int a, int b, float u, string id,
            out Vector3 pos, out Quaternion rot, out Vector3 scl)
        {
            KeyframeChannel ca = ChannelAt(ks, a, id);
            KeyframeChannel cb = ChannelAt(ks, b, id);
            Vector3 pa = ca != null ? PoseUtil.ToVector3(ca.position, Vector3.zero) : Vector3.zero;
            Vector3 pb = cb != null ? PoseUtil.ToVector3(cb.position, Vector3.zero) : Vector3.zero;
            pos = Vector3.Lerp(pa, pb, u);
            Quaternion qa = ChannelRot(ca);
            Quaternion qb = ChannelRot(cb);
            rot = Quaternion.Slerp(qa, qb, u);
            Vector3 sa = ca != null ? PoseUtil.ToVector3(ca.scale, Vector3.one) : Vector3.one;
            Vector3 sb = cb != null ? PoseUtil.ToVector3(cb.scale, Vector3.one) : Vector3.one;
            scl = Vector3.Lerp(sa, sb, u);
        }

        // A channel's rotation offset: quaternion if provided (avoids euler gimbal/order issues),
        // else the euler `rotation`. Identity for a missing channel.
        static Quaternion ChannelRot(KeyframeChannel c)
        {
            if (c == null) return Quaternion.identity;
            if (c.quat != null && c.quat.Length == 4)
            {
                Quaternion q = new Quaternion(c.quat[0], c.quat[1], c.quat[2], c.quat[3]);
                float m = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
                if (m > 1e-6f) return q;
            }
            return Quaternion.Euler(PoseUtil.ToVector3(c.rotation, Vector3.zero));
        }

        // Interpolate a blendshape channel's weight between keyframes a and b.
        static float SampleBlendChannel(List<PoseKeyframe> ks, int a, int b, float u, string id)
        {
            KeyframeChannel ca = ChannelAt(ks, a, id);
            KeyframeChannel cb = ChannelAt(ks, b, id);
            float wa = ca != null ? ca.weight : 0f;
            float wb = cb != null ? cb.weight : 0f;
            return Mathf.Lerp(wa, wb, u);
        }

        public void Apply(float dt)
        {
            if (config == null) return;

            // 1) Advance per-item envelopes / phases.
            for (int i = 0; i < items.Count; i++)
            {
                ItemRT rt = items[i];
                PoseItem it = rt.item;

                if (it.useTrigger && rt.trigR != null && rt.trigIndex >= 0 && rt.trigR.sharedMesh != null)
                {
                    // Blendshape-driven: map the source weight straight onto env (no easing,
                    // so the response tracks the blendshape 1:1) through curve + strength.
                    float w = rt.trigR.GetBlendShapeWeight(rt.trigIndex); // 0..100 (may exceed)
                    float raw = Mathf.Clamp01(w * 0.01f);
                    float curved = Mathf.Pow(raw, Mathf.Max(0.01f, it.triggerCurve));
                    rt.env = Mathf.Clamp01(curved * (it.triggerScale * 0.01f));
                }
                else
                {
                    float target = it.active ? 1f : 0f;
                    float bt = Mathf.Max(0.0001f, it.blendTime);
                    rt.env = Mathf.MoveTowards(rt.env, target, dt / bt);
                }

                if (it.type == "animation")
                {
                    if (it.useKeyframes && it.keyframes != null && it.keyframes.Count > 0)
                    {
                        // Keyframe timeline: phase tracks elapsed seconds, looped over the
                        // total cycle. Each keyframe holds its own pose, so we resolve the
                        // two keyframes we're between (kfA->kfB, kfU) and let every bind
                        // interpolate its OWN channel between those poses. `env` is the
                        // overall fade-in/out strength applied on top.
                        float cycle = KeyCycle(it.keyframes);
                        // Anim Speed acts as a universal playback-rate multiplier here
                        // (1 = authored timing, 0.5 = half speed, 2 = double), it was
                        // only wired into the wave mode below, so keyframe animations
                        // (incl. imported MMD/BVH/VRMA) ignored the slider entirely.
                        if (!rt.paused) rt.phase += Mathf.Max(0f, it.speed) * dt;
                        if (cycle > 0.0001f) rt.phase -= cycle * Mathf.Floor(rt.phase / cycle);
                        else rt.phase = 0f;
                        rt.kfMode = true;
                        ComputeSegment(it.keyframes, rt.phase, cycle, out rt.kfA, out rt.kfB, out rt.kfU);
                        // per-segment easing (timeline editor): remap the blend fraction
                        if (rt.kfA >= 0 && rt.kfA < it.keyframes.Count && it.keyframes[rt.kfA] != null)
                            rt.kfU = EaseRemap(it.keyframes[rt.kfA].ease, rt.kfU);
                        rt.amount = rt.env;
                    }
                    else
                    {
                        rt.kfMode = false;
                        rt.phase += Mathf.Max(0f, it.speed) * dt;
                        rt.phase -= Mathf.Floor(rt.phase);
                        rt.amount = rt.env * Wave(it.waveform, rt.phase);
                    }
                }
                else
                {
                    rt.kfMode = false;
                    rt.amount = rt.env;
                }
            }

            // 2) Bone groups.
            for (int gi = 0; gi < boneGroups.Count; gi++)
            {
                BoneGroup g = boneGroups[gi];
                if (g.t == null) continue;

                float maxAmt = 0f;
                Vector3 sumPos = Vector3.zero;
                Quaternion combRot = Quaternion.identity;
                Vector3 combScale = Vector3.one;
                bool anyScale = false;

                for (int bi = 0; bi < g.binds.Count; bi++)
                {
                    BoneBind bind = g.binds[bi];
                    float a = bind.rt.amount;
                    if (a <= EPS) continue;
                    if (a > maxAmt) maxAmt = a;

                    ITransformTarget tg = bind.target;

                    // Full-strength offset for this bind. In keyframe mode it comes from
                    // interpolating this target's channel between the two active keyframe
                    // poses; otherwise it's the target's single static offset.
                    Vector3 offPos; Quaternion offRot; Vector3 offScl;
                    if (bind.rt.kfMode)
                    {
                        SampleTransformChannel(bind.rt.item.keyframes, bind.rt.kfA, bind.rt.kfB, bind.rt.kfU,
                            bind.channelId, out offPos, out offRot, out offScl);
                    }
                    else
                    {
                        offPos = PoseUtil.ToVector3(tg.Pos, Vector3.zero);
                        offRot = Quaternion.Euler(PoseUtil.ToVector3(tg.Rot, Vector3.zero));
                        offScl = PoseUtil.ToVector3(tg.Scl, Vector3.one);
                    }

                    if (bind.retarget)
                    {
                        offRot = bind.rq * offRot * Quaternion.Inverse(bind.rq);
                        offPos = Vector3.Scale(offPos, bind.posScale);
                        if (bind.strength != 1f)
                            offRot = Quaternion.Slerp(Quaternion.identity, offRot, bind.strength);
                    }

                    offPos = Vector3.Scale(offPos, bind.fitScale) + bind.fitPos;
                    offRot = bind.fitRot * offRot;

                    if (tg.UsePosition)
                        sumPos += offPos * a;
                    if (tg.UseRotation)
                        combRot = combRot * Quaternion.Slerp(Quaternion.identity, offRot, a);
                    if (tg.UseScale)
                    {
                        combScale = Vector3.Scale(combScale, Vector3.Lerp(Vector3.one, offScl, a));
                        anyScale = true;
                    }
                }

                if (maxAmt <= EPS)
                {
                    g.engaged = false; // released, Animator drives this bone again
                    g.usesScale = false;
                    continue;
                }

                if (!g.engaged)
                {
                    g.engaged = true;
                    g.basePos = g.t.localPosition;
                    g.baseRot = g.t.localRotation;
                    g.baseScale = g.t.localScale;
                }

                g.t.localPosition = g.basePos + sumPos;
                g.t.localRotation = g.baseRot * combRot;
                g.t.localScale = Vector3.Scale(g.baseScale, combScale);
                g.usesScale = anyScale;
                g.lastScale = g.t.localScale;
            }

            // 2.5) IK goals, solved AFTER FK so the hips/spine/chest pose is already in
            // place; the solver then overrides each limb's three bones to hit its pinned
            // target (feet planted in root space, hands held in chest space, etc.).
            for (int i = 0; i < ikBinds.Count; i++)
            {
                IKBind ib = ikBinds[i];
                float a = ib.rt.amount * Mathf.Clamp01(ib.goal.weight);
                if (a <= EPS) { ib.engaged = false; continue; }
                if (ib.up == null || ib.mid == null || ib.end == null) continue;

                if (!ib.engaged)
                {
                    ib.engaged = true;
                    ib.baseUp = ib.up.localRotation;
                    ib.baseMid = ib.mid.localRotation;
                    ib.baseEnd = ib.end.localRotation;
                }

                // Capture the target the first time this goal runs (after FK), so the limb
                // holds wherever it currently is, expressed relative to its reference space.
                if (!ib.captured)
                {
                    ib.captured = true;
                    if (ib.space != null)
                    {
                        ib.tgtPos = ib.space.InverseTransformPoint(ib.end.position);
                        ib.tgtRot = Quaternion.Inverse(ib.space.rotation) * ib.end.rotation;
                    }
                    else { ib.tgtPos = ib.end.position; ib.tgtRot = ib.end.rotation; }
                    ib.tgtPos += ib.fitPos;
                }

                // Animated offset (in `space`) added to the captured base target this keyframe.
                Vector3 dPos = Vector3.zero; Quaternion dRot = Quaternion.identity;
                if (ib.animated && ib.rt.kfMode && ib.rt.item != null && ib.rt.item.keyframes != null)
                {
                    Vector3 sp; Quaternion sr; Vector3 ss;
                    SampleTransformChannel(ib.rt.item.keyframes, ib.rt.kfA, ib.rt.kfB, ib.rt.kfU,
                        ib.channelId, out sp, out sr, out ss);
                    dPos = sp; dRot = sr;
                    if (ib.retarget)
                    {
                        dPos = Vector3.Scale(dPos, ib.posScale);
                        dRot = ib.rq * dRot * Quaternion.Inverse(ib.rq);
                    }
                    dPos = Vector3.Scale(dPos, ib.fitScale);
                }

                Vector3 worldTarget;
                Quaternion worldRot;
                if (ib.space != null)
                {
                    worldTarget = ib.space.TransformPoint(ib.tgtPos + dPos);
                    worldRot = ib.space.rotation * (ib.tgtRot * dRot);
                }
                else { worldTarget = ib.tgtPos + dPos; worldRot = ib.tgtRot * dRot; }

                SolveTwoBoneIK(ib.up, ib.mid, ib.end, worldTarget, a);
                if (ib.goal.holdRotation)
                    ib.end.rotation = Quaternion.Slerp(ib.end.rotation, worldRot, a);
            }

            // 3a) VRM clips: work out each clip's extra amount; the plugin's expression listener
            // adds it. A clip with nothing driving it drops out, so tracking is left alone.
            for (int ci = 0; ci < clipGroups.Count; ci++)
            {
                ClipGroup cg = clipGroups[ci];
                float sum = 0f;
                bool any = false;
                for (int bi = 0; bi < cg.binds.Count; bi++)
                {
                    BlendBind bind = cg.binds[bi];
                    float a = bind.rt.amount;
                    if (a <= EPS) continue;
                    any = true;
                    float w = bind.rt.kfMode
                        ? SampleBlendChannel(bind.rt.item.keyframes, bind.rt.kfA, bind.rt.kfB, bind.rt.kfU, bind.channelId)
                        : bind.target.weight;
                    sum += a * w;
                }
                // VNyan only accepts a clip from zero up, so it gets that part, and anything
                // below zero is pushed onto the clip's own mesh shapes (ApplyClipNegatives).
                cg.neg = any ? Mathf.Min(sum, 0f) : 0f;
                if (any && sum > 0f) clipValues[cg.clip] = sum / 100f;   // stored 0..100 like mesh shapes
                else clipValues.Remove(cg.clip);
            }

            // 3) Blendshape groups.
            for (int gi = 0; gi < blendGroups.Count; gi++)
            {
                BlendGroup g = blendGroups[gi];
                if (g.r == null) continue;

                float maxAmt = 0f;
                float sum = 0f;
                for (int bi = 0; bi < g.binds.Count; bi++)
                {
                    BlendBind bind = g.binds[bi];
                    float a = bind.rt.amount;
                    if (a <= EPS) continue;
                    if (a > maxAmt) maxAmt = a;
                    float w = bind.rt.kfMode
                        ? SampleBlendChannel(bind.rt.item.keyframes, bind.rt.kfA, bind.rt.kfB, bind.rt.kfU, bind.channelId)
                        : bind.target.weight;
                    sum += a * w;
                }

                // Only worked out here. The write happens in ApplyLateBlends, after VNyan's
                // expression pass, which rewrites every clip-driven shape in its LateUpdate.
                g.want = maxAmt > EPS;
                g.sum = sum;
            }

            // 4) Hide meshes, disable a renderer while any item that hides it is active.
            for (int gi = 0; gi < hideGroups.Count; gi++)
            {
                HideGroup g = hideGroups[gi];
                if (g.r == null) continue;
                bool wantHide = false;
                for (int k = 0; k < g.items.Count; k++)
                    if (g.items[k].amount > 0.5f) { wantHide = true; break; }
                if (wantHide && !g.hidden)
                {
                    g.baseEnabled = g.r.enabled;
                    g.r.enabled = false;
                    g.hidden = true;
                }
                else if (!wantHide && g.hidden)
                {
                    // A Pose Studio toggle that is switched off has the final say: the mesh
                    // comes back even if Weight Studio (or the shared hidden list) had hidden
                    // it, and it leaves that shared list so it is not re-hidden a moment later.
                    g.r.enabled = true;
                    g.hidden = false;
                    unhideShared.Add(g.r.name);
                }
            }
            if (unhideShared.Count > 0) { RemoveFromSharedHidden(unhideShared); unhideShared.Clear(); }
        }

        readonly HashSet<string> unhideShared = new HashSet<string>();

        // hidden_meshes.txt in VNyan's AppData is shared by Weight Studio and the soft-body
        // plugins: Weight Studio re-hides every name on it every couple of seconds. Taking a
        // name off is how a mesh Pose Studio just showed stays shown. Every other line is kept.
        static void RemoveFromSharedHidden(HashSet<string> names)
        {
            try
            {
                string p = System.IO.Path.Combine(Application.persistentDataPath, "hidden_meshes.txt");
                if (!System.IO.File.Exists(p)) return;
                string[] lines = System.IO.File.ReadAllLines(p);
                List<string> keep = new List<string>();
                List<string> dropped = new List<string>();
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0) continue;
                    if (names.Contains(t)) dropped.Add(t); else keep.Add(lines[i]);
                }
                if (dropped.Count == 0) return;
                System.IO.File.WriteAllLines(p, keep.ToArray());
                Debug.Log("[PoseStudio] shown again and taken off the shared hidden list: " + string.Join(", ", dropped.ToArray()));
            }
            catch (System.Exception e) { Debug.LogWarning("[PoseStudio] could not update hidden_meshes.txt: " + e.Message); }
        }

        // Re-apply Pose Studio's bone SCALE after the physics have run this frame. Pose Studio
        // itself applies EARLY (so spring/jiggle physics react to the pose motion), but jiggle
        // then OVERWRITES localScale on bones it squash/stretches, which are often the same
        // bones a pose scales (e.g. a "bigger chest" toggle on the breast root). Called from a
        // late-execution-order component so the pose's scale is the final word on those bones.
        static float EaseRemap(string ease, float u)
        {
            if (ease == "smooth") return u * u * (3f - 2f * u);
            if (ease == "in") return u * u;
            if (ease == "out") return 1f - (1f - u) * (1f - u);
            return u;
        }

        // ---- timeline editor hooks: scrub / transport on a specific item ----
        ItemRT FindRT(PoseItem it)
        {
            for (int i = 0; i < items.Count; i++) if (items[i].item == it) return items[i];
            return null;
        }
        public float GetPhase(PoseItem it) { ItemRT rt = FindRT(it); return rt != null ? rt.phase : 0f; }
        public void SetPhase(PoseItem it, float seconds)
        { ItemRT rt = FindRT(it); if (rt != null) rt.phase = Mathf.Max(0f, seconds); }
        public bool GetPaused(PoseItem it) { ItemRT rt = FindRT(it); return rt != null && rt.paused; }
        public void SetPaused(PoseItem it, bool p) { ItemRT rt = FindRT(it); if (rt != null) rt.paused = p; }

        // Runs once a frame AFTER every script's LateUpdate and before meshes are skinned (a
        // PostLateUpdate player-loop step installed by the plugin). VNyan's expression pass has
        // finished by now, so whatever it wrote is this frame's base and Pose Studio adds on top:
        // tracking keeps showing underneath, and nothing paints over the result before it is
        // drawn. Soft-body plugins bake at end of frame, so they see it too.
        public void ApplyLateBlends()
        {
            for (int gi = 0; gi < blendGroups.Count; gi++)
            {
                BlendGroup g = blendGroups[gi];
                if (g.r == null) continue;
                float cur = g.r.GetBlendShapeWeight(g.index);
                if (g.want)
                {
                    // Someone else wrote it since our last write (VNyan's clips, tracking): that
                    // value is the new base. Otherwise keep the base, so we never stack up.
                    if (!g.engaged || Mathf.Abs(cur - g.lastW) > 0.001f) g.baseWeight = cur;
                    g.engaged = true;
                    // Past 100% and below 0% are allowed on purpose: Unity extends a blendshape
                    // linearly either way, which is how a shape gets exaggerated or reversed.
                    float v = Mathf.Clamp(g.baseWeight + g.sum, -BLEND_LIMIT, BLEND_LIMIT);
                    g.r.SetBlendShapeWeight(g.index, v);
                    g.lastW = v;
                }
                else if (g.engaged)
                {
                    // Put the shape back where we found it, unless something else has taken it
                    // over since (then leave theirs). Without this a face-tracked shape the item
                    // touched (a blink, say) would stick once the item stops.
                    if (Mathf.Abs(cur - g.lastW) <= 0.001f) g.r.SetBlendShapeWeight(g.index, g.baseWeight);
                    g.engaged = false;
                }
            }
            ApplyClipNegatives();
        }

        // Set by the plugin if the post-LateUpdate hook could not be installed: blendshapes are
        // then written from the late LateUpdate component instead, so they still apply.
        public static bool lateBlendFallback;

        public void ApplyLateScale()
        {
            if (lateBlendFallback) ApplyLateBlends();
            for (int gi = 0; gi < boneGroups.Count; gi++)
            {
                BoneGroup g = boneGroups[gi];
                if (g.engaged && g.usesScale && g.t != null)
                    g.t.localScale = g.lastScale;
            }
        }

        static float SafeAngle(Vector3 u, Vector3 v)
        {
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(u.normalized, v.normalized), -1f, 1f));
        }

        // Analytic 2-bone IK in world space. Bends the mid joint to the reach distance, then
        // aims the upper bone so the end lands on the target. `weight` blends the result over
        // the incoming (FK) pose. Bend direction is self-correcting (tries +; flips if the
        // reach didn't match), so it's robust to the hinge axis sign. Knee/elbow direction is
        // left as-is (good for pins, where the target sits near the current pose).
        static void SolveTwoBoneIK(Transform up, Transform mid, Transform end, Vector3 target, float weight)
        {
            if (up == null || mid == null || end == null) return;
            Quaternion upOrig = up.rotation;
            Quaternion midOrig = mid.rotation;

            Vector3 a = up.position;
            Vector3 b = mid.position;
            Vector3 c = end.position;

            float lab = Vector3.Distance(a, b);
            float lcb = Vector3.Distance(b, c);
            if (lab < 1e-5f || lcb < 1e-5f) return;
            float lat = Mathf.Clamp(Vector3.Distance(a, target), 1e-4f, lab + lcb - 1e-4f);

            // 1) Bend the mid joint to set the interior angle for the desired reach.
            float baBc0 = SafeAngle(a - b, c - b);
            float baBc1 = Mathf.Acos(Mathf.Clamp((lab * lab + lcb * lcb - lat * lat) / (2f * lab * lcb), -1f, 1f));
            float bendDeg = (baBc1 - baBc0) * Mathf.Rad2Deg;

            Vector3 hinge = Vector3.Cross(a - b, c - b);
            if (hinge.sqrMagnitude < 1e-8f) hinge = Vector3.Cross(a - b, target - b);
            if (hinge.sqrMagnitude < 1e-8f) hinge = Vector3.up;
            hinge = hinge.normalized;

            mid.rotation = Quaternion.AngleAxis(bendDeg, hinge) * mid.rotation;
            if (Mathf.Abs(Vector3.Distance(a, end.position) - lat) > 0.01f)
                mid.rotation = Quaternion.AngleAxis(-2f * bendDeg, hinge) * mid.rotation;

            // 2) Aim the upper bone so the end effector lands on the target.
            Vector3 ac2 = end.position - a;
            if (ac2.sqrMagnitude > 1e-10f)
            {
                Quaternion aim = Quaternion.FromToRotation(ac2, target - a);
                up.rotation = aim * up.rotation;
            }

            // 3) Weight blend over the incoming FK pose.
            if (weight < 0.999f)
            {
                up.rotation = Quaternion.Slerp(upOrig, up.rotation, weight);
                mid.rotation = Quaternion.Slerp(midOrig, mid.rotation, weight);
            }
        }

        // Release every engaged bone/shape back to its captured base (used when disabling).
        public void Restore()
        {
            for (int i = 0; i < boneGroups.Count; i++)
            {
                BoneGroup g = boneGroups[i];
                if (g.engaged && g.t != null)
                {
                    g.t.localPosition = g.basePos;
                    g.t.localRotation = g.baseRot;
                    g.t.localScale = g.baseScale;
                }
                g.engaged = false;
            }
            for (int i = 0; i < ikBinds.Count; i++)
            {
                IKBind ib = ikBinds[i];
                if (ib.engaged)
                {
                    if (ib.up != null) ib.up.localRotation = ib.baseUp;
                    if (ib.mid != null) ib.mid.localRotation = ib.baseMid;
                    if (ib.end != null) ib.end.localRotation = ib.baseEnd;
                }
                ib.engaged = false;
            }
            for (int i = 0; i < blendGroups.Count; i++)
            {
                BlendGroup g = blendGroups[i];
                if (g.engaged && g.r != null)
                    g.r.SetBlendShapeWeight(g.index, g.baseWeight);
                g.engaged = false;
                g.want = false;
            }
            clipValues.Clear();      // nothing added to the expressions any more
            for (int ci = 0; ci < clipGroups.Count; ci++)
            {
                clipGroups[ci].neg = 0f;
                for (int m = 0; m < clipGroups[ci].meshes.Count; m++)
                {
                    ClipMesh cm = clipGroups[ci].meshes[m];
                    if (cm.engaged && cm.r != null) cm.r.SetBlendShapeWeight(cm.index, cm.baseW);
                    cm.engaged = false;
                }
            }
            for (int i = 0; i < hideGroups.Count; i++)
            {
                HideGroup g = hideGroups[i];
                if (g.hidden && g.r != null) g.r.enabled = g.baseEnabled;
                g.hidden = false;
            }
        }
    }

    // Runs LATE (after physics) and re-applies the pose's bone scale, so a pose that scales a
    // jiggle bone (e.g. bigger chest) isn't overwritten by the jiggle solver's own scale.
    [DefaultExecutionOrder(10000)]
    public class PoseStudioLateScale : MonoBehaviour
    {
        public PoseApplier applier;
        void LateUpdate() { if (applier != null) applier.ApplyLateScale(); }
    }
}
