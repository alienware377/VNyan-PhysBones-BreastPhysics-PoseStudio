using System.Collections.Generic;
using Newtonsoft.Json;

namespace JayoPoseStudio
{
    // ---------------------------------------------------------------------------
    // Shareable, human-editable config for the Pose Studio plugin.
    //
    // A "PoseItem" is either a TOGGLE (hold a pose / blendshape set on or off) or an
    // ANIMATION (smoothly oscillate that same pose on and off on a loop). Both share the
    // exact same editing surface — a set of transform offsets (bones AND meshes) and
    // blendshape weights — which keeps the UI simple while still covering toggles,
    // expressions, and looping idles.
    // ---------------------------------------------------------------------------

    public class PoseSettings
    {
        public bool enabled = true;
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
        public float[] position = { 0f, 0f, 0f }; // local position OFFSET (metres)   — transform channels
        public float[] rotation = { 0f, 0f, 0f }; // local euler OFFSET (degrees)      — transform channels
        public float[] scale = { 1f, 1f, 1f };    // local scale MULTIPLIER            — transform channels
        public float weight = 100f;               // 0..100 blendshape weight          — blend channels
    }

    // One keyframe in an animation's timeline. `seconds` is the transition time FROM this
    // keyframe TO the next one (the last keyframe's seconds loops back to the first). Each
    // keyframe carries its OWN full pose as a list of channels, so the animation can move
    // through a series of distinct poses rather than just fading a single pose in and out.
    public class PoseKeyframe
    {
        public float seconds = 0.5f;              // seconds from this keyframe to the next (3-decimal)
        public List<KeyframeChannel> channels = new List<KeyframeChannel>();
    }

    // Helpers for building/finding keyframe channels by a stable string id. Kept C#5-safe
    // (no expression bodies / string interpolation) to match the rest of the codebase.
    public static class KeyChannels
    {
        public static string BoneId(string bone) { return "bone:" + bone; }
        public static string MeshId(string mesh) { return "mesh:" + mesh; }
        public static string BlendId(string mesh, string shape) { return "blend:" + mesh + "::" + shape; }

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
    // wherever it started — e.g. feet pinned in root space stay planted as the hips sway;
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

    public class PoseItem
    {
        public string name = "item";
        public string type = "toggle";   // "toggle" or "animation"
        public bool active = false;      // toggle on/off, or animation playing
        public float blendTime = 0.25f;  // ease seconds (toggle on/off, anim fade in/out)
        public float speed = 1.0f;       // animation cycles per second (wave mode)
        public string waveform = "sine"; // "sine" | "triangle" | "pulse"
        public string hotkey = "";       // e.g. "F8" or "Ctrl+Shift+E"; empty = unbound

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
        // directly as this item's amount — so X% of the blendshape becomes X% of the
        // toggle/animation. When enabled, this replaces the on/off "active" control.
        public bool useTrigger = false;     // drive strength from a blendshape
        public string triggerMesh = "";     // source SkinnedMeshRenderer name (empty = search any)
        public string triggerShape = "";    // source blendshape name
        public float triggerCurve = 1.0f;   // response exponent: 1 = linear, >1 ease-in, <1 ease-out
        public float triggerScale = 100f;   // overall output strength percentage (0..100)

        public List<BoneTarget> bones = new List<BoneTarget>();
        public List<MeshTarget> meshes = new List<MeshTarget>();
        public List<BlendTarget> blendshapes = new List<BlendTarget>();

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
