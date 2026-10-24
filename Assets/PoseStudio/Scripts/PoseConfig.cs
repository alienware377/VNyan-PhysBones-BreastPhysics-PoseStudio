using System.Collections.Generic;
using Newtonsoft.Json;

namespace PoseStudio
{
    // ---------------------------------------------------------------------------
    // Shareable, human-editable config for the Pose Studio plugin.
    //
    // A "PoseItem" is either a TOGGLE (hold a pose / blendshape set on or off) or an
    // ANIMATION (smoothly oscillate that same pose on and off on a loop). Both share the
    // exact same editing surface, a set of transform offsets (bones AND meshes) and
    // blendshape weights, which keeps the UI simple while still covering toggles,
    // expressions, and looping idles.
    // ---------------------------------------------------------------------------

    public class PoseSettings
    {
        public bool enabled = true;

        // ----- Idle animation (auto-play on tracking loss) -----
        // Tracking loss is detected by watching the head bone for fresh tracker motion: if it
        // hasn't moved more than `idleMotionThreshold` degrees/frame for `idleDelay` seconds,
        // tracking is considered lost and the item flagged `idleAnim` auto-plays (unless a
        // "major" animation is already on). Both are JSON-tunable.
        public float idleDelay = 15f;              // seconds of stillness before idle kicks in
        public float idleMotionThreshold = 0.02f;  // degrees/frame of head motion = "tracking alive"

        // Names of the animations that ship with the plugin which were already offered once,
        // so a deleted or renamed one is not added back on the next load.
        public List<string> bundledOffered = new List<string>();
    }

    // Common surface for anything that offsets a Transform (a bone or a mesh object).
    // The applier reads these to layer a position/rotation/scale offset on top of the
    // target's captured rest transform. Marked JsonIgnore so the interface doesn't add
    // duplicate keys to the serialized config (the concrete fields are what persist).
    public interface ITransformTarget
    {
        [JsonIgnore] string TargetName { get; }
        [JsonIgnore] bool UsePosition { get; }
        [JsonIgnore] bool UseRotation { get; }
        [JsonIgnore] bool UseScale { get; }
        [JsonIgnore] float[] Pos { get; }
        [JsonIgnore] float[] Rot { get; }
        [JsonIgnore] float[] Scl { get; }
    }

    // A single bone's contribution at full strength (relative to the bone's rest pose).
    public class BoneTarget : ITransformTarget
    {
        public string bone;                       // bone/transform name (or humanoid enum)
        public bool usePosition = false;
        public bool useRotation = true;
        public bool useScale = false;
        public float[] position = { 0f, 0f, 0f }; // local position OFFSET (metres)
        public float[] rotation = { 0f, 0f, 0f }; // local euler OFFSET (degrees)
        public float[] scale = { 1f, 1f, 1f };    // local scale MULTIPLIER (1 = unchanged)

        [JsonIgnore] public string TargetName { get { return bone; } }
        [JsonIgnore] public bool UsePosition { get { return usePosition; } }
        [JsonIgnore] public bool UseRotation { get { return useRotation; } }
        [JsonIgnore] public bool UseScale { get { return useScale; } }
        [JsonIgnore] public float[] Pos { get { return position; } }
        [JsonIgnore] public float[] Rot { get { return rotation; } }
        [JsonIgnore] public float[] Scl { get { return scale; } }
    }

    // A single mesh object's transform contribution at full strength. Targets the
    // GameObject transform that owns a renderer (e.g. a clothing/accessory mesh).
    // Position defaults ON because moving/scaling a mesh object is the common case.
    public class MeshTarget : ITransformTarget
    {
        public string mesh;                       // renderer GameObject name
        public bool usePosition = true;
        public bool useRotation = false;
        public bool useScale = false;
        public float[] position = { 0f, 0f, 0f };
        public float[] rotation = { 0f, 0f, 0f };
        public float[] scale = { 1f, 1f, 1f };

        [JsonIgnore] public string TargetName { get { return mesh; } }
        [JsonIgnore] public bool UsePosition { get { return usePosition; } }
        [JsonIgnore] public bool UseRotation { get { return useRotation; } }
        [JsonIgnore] public bool UseScale { get { return useScale; } }
        [JsonIgnore] public float[] Pos { get { return position; } }
        [JsonIgnore] public float[] Rot { get { return rotation; } }
        [JsonIgnore] public float[] Scl { get { return scale; } }
    }

    // A single blendshape's target weight at full strength.
    public class BlendTarget
    {
        // A VRM blendshape CLIP (Joy, A, Blink...) rather than a raw mesh shape is stored with
        // this as its mesh. Everything else (weight, keyframe channels, the timeline) is shared,
        // only the applier routes it to VNyan's expression system instead of a renderer.
        public const string VRM_CLIP = "@VRM";
        [JsonIgnore] public bool IsClip { get { return mesh == VRM_CLIP; } }

        public string mesh;          // SkinnedMeshRenderer name (empty = search every mesh)
        public string shape;         // blendshape name
        public float weight = 100f;  // 0..100 target weight at full strength
    }

    // A single transform/blendshape channel's value WITHIN one keyframe's pose. The `id`
    // identifies what this channel drives (see KeyChannels.BoneId/MeshId/BlendId). For a
    // bone or mesh channel the position/rotation/scale arrays hold that target's offset at
    // this keyframe; for a blendshape channel only `weight` is used. This lets every
    // keyframe store its own DISTINCT pose, which the applier interpolates between.
    public class KeyframeChannel
    {
        public string id;                         // "bone:Name" | "mesh:Name" | "blend:Mesh::Shape"
        public float[] position = { 0f, 0f, 0f }; // local position OFFSET (metres)  (transform channels)
        public float[] rotation = { 0f, 0f, 0f }; // local euler OFFSET (degrees)  (transform channels)
        public float[] quat = null;               // local rotation OFFSET as quaternion [x,y,z,w]; if set,
                                                   // it OVERRIDES `rotation` (slerped), avoids euler gimbal/order issues
        public float[] scale = { 1f, 1f, 1f };    // local scale MULTIPLIER  (transform channels)
        public float weight = 100f;               // 0..100 blendshape weight  (blend channels)
    }

    // One keyframe in an animation's timeline. `seconds` is the transition time FROM this
    // keyframe TO the next one (the last keyframe's seconds loops back to the first). Each
    // keyframe carries its OWN full pose as a list of channels, so the animation can move
    // through a series of distinct poses rather than just fading a single pose in and out.
    public class PoseKeyframe
    {
        public float seconds = 0.5f;              // seconds from this keyframe to the next (3-decimal)
        public string ease = "linear";            // easing INTO the next key: linear|smooth|in|out
        public List<KeyframeChannel> channels = new List<KeyframeChannel>();
    }

    // Helpers for building/finding keyframe channels by a stable string id. Kept C#5-safe
    // (no expression bodies / string interpolation) to match the rest of the codebase.
    public static class KeyChannels
    {
        public static string BoneId(string bone) { return "bone:" + bone; }
        public static string MeshId(string mesh) { return "mesh:" + mesh; }
        public static string BlendId(string mesh, string shape) { return "blend:" + mesh + "::" + shape; }
        // IK target OFFSET channel: an animated per-keyframe delta (position metres / rotation
        // euler) added to the goal's CAPTURED rest target, expressed in the goal's `space`.
        // Offset 0 = the limb holds its captured pin (the static behaviour); a non-zero offset
        // moves the pin per keyframe (e.g. lift a foot for a kick, then plant it again).
        public static string IkId(string goalName) { return "ik:" + goalName; }

        public static KeyframeChannel Find(PoseKeyframe key, string id)
        {
            if (key == null || key.channels == null) return null;
            for (int i = 0; i < key.channels.Count; i++)
            {
                if (key.channels[i] != null && key.channels[i].id == id) return key.channels[i];
            }
            return null;
        }

        // Get the transform channel for `id`, creating it (seeded from the supplied
        // arrays) if it does not already exist on this keyframe.
        public static KeyframeChannel GetOrCreateTransform(PoseKeyframe key, string id, float[] seedPos, float[] seedRot, float[] seedScl)
        {
            KeyframeChannel c = Find(key, id);
            if (c == null)
            {
                c = new KeyframeChannel();
                c.id = id;
                c.position = Copy3(seedPos, 0f);
                c.rotation = Copy3(seedRot, 0f);
                c.scale = Copy3(seedScl, 1f);
                key.channels.Add(c);
            }
            return c;
        }

        public static KeyframeChannel GetOrCreateBlend(PoseKeyframe key, string id, float seedWeight)
        {
            KeyframeChannel c = Find(key, id);
            if (c == null)
            {
                c = new KeyframeChannel();
                c.id = id;
                c.weight = seedWeight;
                key.channels.Add(c);
            }
            return c;
        }

        // Deep copy a channel list (used when adding a keyframe that duplicates the
        // current one so the new keyframe starts from the same pose).
        public static List<KeyframeChannel> CloneChannels(List<KeyframeChannel> src)
        {
            List<KeyframeChannel> outList = new List<KeyframeChannel>();
            if (src == null) return outList;
            for (int i = 0; i < src.Count; i++)
            {
                KeyframeChannel s = src[i];
                if (s == null) continue;
                KeyframeChannel c = new KeyframeChannel();
                c.id = s.id;
                c.position = Copy3(s.position, 0f);
                c.rotation = Copy3(s.rotation, 0f);
                c.quat = s.quat != null && s.quat.Length == 4
                    ? new float[] { s.quat[0], s.quat[1], s.quat[2], s.quat[3] } : null;
                c.scale = Copy3(s.scale, 1f);
                c.weight = s.weight;
                outList.Add(c);
            }
            return outList;
        }

        static float[] Copy3(float[] src, float fallback)
        {
            float[] outArr = new float[3];
            for (int i = 0; i < 3; i++)
            {
                outArr[i] = (src != null && i < src.Length) ? src[i] : fallback;
            }
            return outArr;
        }
    }

    // A 2-bone IK goal: pin an end effector (foot/hand) to a target so the limb stays put
    // while the body moves. The solver computes upper/lower/end bone rotations in LateUpdate
    // (after FK), overriding whatever the Animator / FK pose did to those three bones.
    // Targets are normally CAPTURED at first play (relative to `space`) so the limb holds
    // wherever it started, e.g. feet pinned in root space stay planted as the hips sway;
    // hands pinned in chest space stay crossed but follow the torso twist.
    public class IKGoal
    {
        public bool enabled = true;
        public string name = "ik";
        public string upper = "";          // e.g. "LeftUpperLeg" / "LeftUpperArm"
        public string lower = "";          // e.g. "LeftLowerLeg" / "LeftLowerArm"
        public string end = "";            // e.g. "LeftFoot" / "Left wrist"
        public string space = "root";      // "root" | "world" | a bone name (e.g. "Chest","Hips")
        public bool holdRotation = true;   // also pin the end's orientation (sole flat / hand angle)
        public float weight = 1f;          // 0..1 blend of the IK result over the FK pose
        public bool capture = true;        // capture the target (relative to space) vs use explicit pos/rot
        public string captureMode = "play"; // "bind" = capture at bind (rest/neutral pose, e.g. feet
                                            //   centered); "play" = capture on first play frame (e.g.
                                            //   hands, which only reach the crossed pose once FK runs)
        public float[] position = { 0f, 0f, 0f }; // explicit target pos in `space` (if capture=false)
        public float[] rotation = { 0f, 0f, 0f }; // explicit target euler in `space` (if capture=false)
    }

    // What the animation was AUTHORED on, so it can be replayed faithfully on a different
    // avatar. Rest world rotations let a bone's local offset be re-expressed in another rig's
    // bone axes; the measurements let position offsets (sway, crouch, stance, hand goals) be
    // scaled to the new body instead of being taken as literal metres.
    public class RigRef
    {
        public float hipHeight = 0f;     // hips above the floor, metres
        public float legLength = 0f;     // thigh + shin
        public float armLength = 0f;     // upper arm + forearm
        // Widths matter as much as lengths: a wider body needs a wider stance and its hands
        // further out, or the feet cross and the arms end up inside the torso.
        public float ankleGap = 0f;      // left ankle to right ankle at rest
        public float shoulderGap = 0f;   // left upper arm to right upper arm at rest
        // bone name -> rest WORLD rotation (x,y,z,w) on the authoring avatar
        public Dictionary<string, float[]> restRot = new Dictionary<string, float[]>();
    }

    // Hand tuning for a whole animation at once, on top of whatever the automatic retarget
    // works out. One number per bone GROUP rather than per keyframe, so an imported or bundled
    // animation can be fitted to a model by eye in a few seconds: widen the stance, bring the
    // hands in, take the crouch out, soften every rotation.
    public class PoseAdjust
    {
        public float bodyWidth = 1f;    // sideways body offsets: sway, stance width
        public float bodyHeight = 1f;   // up/down body offsets: crouch, hops, foot lifts
        public float bodyDepth = 1f;    // forward/back body offsets
        public float armReach = 1f;     // how far the hands sit from the body, sideways
        public float armHeight = 1f;    // hand height and forward reach
        public float strength = 1f;     // how strongly every rotation applies (0 = none)
    }

    // A whole-animation adjustment for one group of bones, so an imported or bundled animation
    // can be fitted to a body it was not made on without touching a single keyframe.
    //   scale     multiplies that group's position offsets per axis (stance width, crouch depth)
    //   position  shifts that group's IK goals in metres (feet further apart, hands further out)
    //   rotation  adds a constant turn to every bone in the group (open the arms a little more)
    // It is an ITransformTarget so the window's existing position / rotation / scale sliders can
    // edit it: it simply appears in the target list as "(fit) Arms".
    public class GroupAdjust : ITransformTarget
    {
        public string group = "Arms";   // Arms | Hands | Legs | Feet | Hips | Spine | Head | All
        public float[] position = { 0f, 0f, 0f };
        public float[] rotation = { 0f, 0f, 0f };
        public float[] scale = { 1f, 1f, 1f };

        [JsonIgnore] public string TargetName { get { return "(fit) " + group; } }
        [JsonIgnore] public bool UsePosition { get { return true; } }
        [JsonIgnore] public bool UseRotation { get { return true; } }
        [JsonIgnore] public bool UseScale { get { return true; } }
        [JsonIgnore] public float[] Pos { get { return position; } }
        [JsonIgnore] public float[] Rot { get { return rotation; } }
        [JsonIgnore] public float[] Scl { get { return scale; } }

        // Which group a bone belongs to. Names are the humanoid ones, so this works on any rig.
        public static string GroupOf(string bone)
        {
            if (string.IsNullOrEmpty(bone)) return "All";
            if (bone.Contains("Hand") || bone.Contains("wrist") || bone.Contains("Thumb")
                || bone.Contains("Index") || bone.Contains("Middle") || bone.Contains("Ring")
                || bone.Contains("Little") || bone.Contains("Pinky")) return "Hands";
            if (bone.Contains("Arm") || bone.Contains("Shoulder")) return "Arms";
            if (bone.Contains("Foot") || bone.Contains("Toe") || bone.Contains("Ankle")) return "Feet";
            if (bone.Contains("Leg") || bone.Contains("Knee")) return "Legs";
            if (bone.Contains("Hips") || bone.Contains("Hip")) return "Hips";
            if (bone.Contains("Head") || bone.Contains("Neck") || bone.Contains("Eye")
                || bone.Contains("Jaw")) return "Head";
            if (bone.Contains("Spine") || bone.Contains("Chest")) return "Spine";
            return "All";
        }
    }

    public class PoseItem
    {
        public string name = "item";
        public string type = "toggle";   // "toggle" or "animation"
        public bool active = false;      // toggle on/off, or animation playing
        public float blendTime = 0.25f;  // ease seconds (toggle on/off, anim fade in/out)
        public float speed = 1.0f;       // animation cycles per second (wave mode)
        public string waveform = "sine"; // "sine" | "triangle" | "pulse"
        public string hotkey = "";       // e.g. "F8" or "Ctrl+Shift+E"; empty = unbound

        // Idle animation: auto-plays when tracking is lost AND no other MAJOR animation is
        // active (major = keyframe animation with >2 keyframes driving >4 major humanoid
        // bones, fingers/toes excluded). Only one item should carry this flag.
        public bool idleAnim = false;

        // ----- Keyframe timeline -----
        // When enabled (animation items only), the item's strength follows a custom
        // timeline of many keyframes instead of the sine/triangle/pulse wave. Each
        // keyframe holds a 0..1 strength and the number of seconds until the next
        // keyframe, so users can author precise, multi-stage looping motions.
        public bool useKeyframes = false;
        public List<PoseKeyframe> keyframes = new List<PoseKeyframe>();

        // ----- Blendshape trigger -----
        // Drive this item's strength continuously from a SOURCE blendshape's weight
        // (e.g. a face-tracked shape). The source's 0..100% weight is normalised to
        // 0..1, shaped by a response curve, scaled by an overall strength %, and used
        // directly as this item's amount, so X% of the blendshape becomes X% of the
        // toggle/animation. When enabled, this replaces the on/off "active" control.
        public bool useTrigger = false;     // drive strength from a blendshape
        public string triggerMesh = "";     // source SkinnedMeshRenderer name (empty = search any)
        public string triggerShape = "";    // source blendshape name
        public float triggerCurve = 1.0f;   // response exponent: 1 = linear, >1 ease-in, <1 ease-out
        public float triggerScale = 100f;   // overall output strength percentage (0..100)

        // ----- Portability -----
        // With a rigRef present and retarget on, every rotation offset is re-expressed in the
        // bound avatar's own bone axes and every position offset is scaled to its proportions,
        // so an animation authored on one model plays correctly on another (VRM, VRChat or
        // Warudo imports included). Turn it off to play the raw authored numbers.
        public bool retarget = true;
        public RigRef rigRef = null;

        // Per-animation fit-up, by bone group. Empty means the animation plays as authored.
        public List<GroupAdjust> fit = new List<GroupAdjust>();
        // Hand tuning. `adjust` is the fallback for any avatar; `adjustPerRig` holds one entry
        // per model, keyed by the rig signature the plugin logs when it binds (limb lengths and
        // widths in centimetres). That way tuning a wide model cannot touch the model the
        // animation was built on, which keeps its own entry or simply needs none.
        public PoseAdjust adjust = null;
        public Dictionary<string, PoseAdjust> adjustPerRig = null;

        public List<BoneTarget> bones = new List<BoneTarget>();
        public List<MeshTarget> meshes = new List<MeshTarget>();
        public List<BlendTarget> blendshapes = new List<BlendTarget>();

        // ----- Hide meshes -----
        // Renderer object names to HIDE (disable) while this item is active, restored when it
        // turns off. Useful for swapping/removing clothing or accessories with a toggle/anim.
        public List<string> hideMeshes = new List<string>();

        // ----- Inverse kinematics -----
        // 2-bone IK goals (feet/hands) applied after FK each frame while this item is active.
        public List<IKGoal> ikGoals = new List<IKGoal>();
    }

    public class PoseConfig
    {
        public PoseSettings settings = new PoseSettings();
        public List<PoseItem> items = new List<PoseItem>();
    }
}
