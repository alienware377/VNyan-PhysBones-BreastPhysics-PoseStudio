using System;
using System.Collections.Generic;

namespace JayoPhysBones
{
    // Plain data classes deserialized from physbones.json (Newtonsoft.Json).
    // All physics values mirror VRChat PhysBone parameters and are normalized 0..1
    // unless noted, so a config authored for a VRChat avatar maps over directly.

    [Serializable]
    public class PhysBoneSettings
    {
        public bool enabled = true;
        public int substeps = 2;          // physics iterations per frame (1..8). Higher = stiffer/stabler.
        public float[] gravityDir = null; // optional world gravity direction override; default is down.

        // When true, the avatar's built-in bone-physics solvers (VRM SpringBone, MagicaCloth,
        // SPCR Joint Dynamics, DynamicBone) are disabled while the plugin is enabled, so only
        // these PhysBones drive the bones. They are restored when the plugin is disabled.
        public bool disableNativePhysics = false;

        // When true (and disableNativePhysics is true), only native solvers that drive bones
        // under one of this config's chain roots are disabled; native physics on every other
        // bone keeps running. When false, every matching native solver on the avatar is disabled.
        public bool nativePhysicsScoped = false;

        // Optional override of the component-type-name substrings matched for the above.
        // null/empty = use the built-in defaults (springbone, dynamicbone, magicacloth, spcrjointdynamics).
        public List<string> nativePhysicsTypes = null;
    }

    [Serializable]
    public class ColliderConfig
    {
        public string name;                       // referenced by chains[].colliders
        public string type = "sphere";            // sphere | capsule | plane
        public string bone;                       // humanoid bone name or transform name to attach to (null = world space)
        public float[] offset = { 0f, 0f, 0f };   // local position offset on the bone
        public float[] offsetEnd = null;          // capsule: second endpoint (local). If set, overrides height/axis.
        public float[] axis = null;               // capsule/plane axis (local). Default up for capsule, used as normal for plane.
        public float radius = 0.1f;               // sphere/capsule radius (plane ignores this)
        public float height = 0f;                 // capsule total height (used when offsetEnd is null)
    }

    [Serializable]
    public class ChainConfig
    {
        public string name = "chain";
        public string rootBone;                   // first bone of the subtree to simulate (humanoid or transform name)
        public List<string> ignore = new List<string>(); // transform names whose subtrees are excluded
        public List<string> colliders = new List<string>(); // collider names this chain collides against

        // --- VRChat PhysBone parameters ---
        public float pull = 0.2f;            // 0..1 force returning bones to their animated/rest pose
        public float spring = 0.2f;          // 0..1 bounciness (higher = more oscillation, less damping)
        public float stiffness = 0.2f;       // 0..1 resistance to bending away from rest direction
        public float gravity = 0f;           // -1..1 world gravity force (negative = upward)
        public float gravityFalloff = 0f;    // 0..1 reduces gravity near the rest pose
        public float immobile = 0f;          // 0..1 how much bones ignore avatar root movement
        public bool immobileWorld = true;    // true = world (ignore translation); reserved for All-Motion later
        public string limitType = "none";    // none | angle
        public float maxAngle = 45f;         // degrees, used when limitType == "angle"
        public float radius = 0f;            // bone collision radius (meters)
        public float maxStretch = 0f;        // 0..1 allowed stretch beyond rest length (0 = rigid)
    }

    [Serializable]
    public class PhysBoneConfig
    {
        public PhysBoneSettings settings = new PhysBoneSettings();
        public List<ColliderConfig> colliders = new List<ColliderConfig>();
        public List<ChainConfig> chains = new List<ChainConfig>();
    }
}
