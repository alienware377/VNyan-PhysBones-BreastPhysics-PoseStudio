using System;
using System.Collections.Generic;

namespace JayoJiggle
{
    // Plain data classes deserialized from jigglephysics.json (Newtonsoft.Json).
    // This is a VNyan-native "jiggle physics" plugin (inspired by VRChat breast-physics
    // assets, but an original implementation): each configured bone gets a single-bone
    // spring solver plus volume-preserving squash-and-stretch deformation.

    [Serializable]
    public class JiggleSettings
    {
        public bool enabled = true;
        public int substeps = 2;            // physics iterations per frame (1..8)
        public float[] gravityDir = null;   // optional world gravity direction override; default down

        // Global clamps / smoothing for the scale (squash & stretch) deformation.
        public float minScale = 0.65f;      // lower clamp applied per-axis to the deform multiplier
        public float maxScale = 1.6f;       // upper clamp applied per-axis to the deform multiplier
        public float scaleSpeed = 14f;      // how fast localScale eases toward its target (per second)

        // When true, the avatar's built-in bone-physics solvers on the jiggle bones are left
        // alone (this plugin only writes rotation+scale, so it can coexist). Reserved for parity
        // with the PhysBones plugin; not used to disable native physics here.
        public bool overrideNative = false;
    }

    [Serializable]
    public class JiggleColliderConfig
    {
        public string name;                       // referenced by bones[].colliders
        public string type = "sphere";            // sphere | capsule | plane
        public string bone;                       // bone/transform to attach to (null = world space)
        public float[] offset = { 0f, 0f, 0f };   // local position offset on the bone
        public float[] offsetEnd = null;          // capsule second endpoint (local)
        public float[] axis = null;               // capsule axis / plane normal (local)
        public float radius = 0.1f;               // sphere/capsule radius
        public float height = 0f;                 // capsule total height (used when offsetEnd is null)
    }

    [Serializable]
    public class JiggleBoneConfig
    {
        public string name = "jiggle";
        public string bone;                       // the bone to simulate (humanoid or transform name)
        public float[] axis = { 0f, 0f, 1f };     // local "forward" the bone points (defines the swing tip)
        public float length = 0.08f;              // virtual tip length in metres (swing arm)

        // --- spring / motion parameters (all 0..1 unless noted) ---
        public float weight = 0.5f;               // 0..1 gravity pull (0 = floaty, 1 = heavy sag)
        public float bounce = 0.6f;               // 0..1 springiness (higher = more oscillation)
        public float stiffness = 0.2f;            // 0..1 resistance to bending from rest direction
        public float damping = 0.1f;              // 0..1 extra velocity loss (higher = settles faster)
        public float pull = 0.15f;                // 0..1 force returning to the rest pose

        // --- bra limiter ---
        public string limitType = "angle";        // none | angle
        public float maxAngle = 60f;              // degrees of swing allowed from rest

        // --- deformation (the squash & stretch layer) ---
        public float stretch = 0.4f;              // 0..1 how much it elongates when sagging
        public float squish = 0.5f;               // 0..1 how much it flattens under collision

        // --- collision ---
        public float radius = 0.04f;              // this bone's collision radius (metres)
        public bool selfCollide = true;           // collide against other selfCollide bones (e.g. L vs R)
        public float selfRadius = 0.06f;          // sphere radius used for self-collision at the tip
        public List<string> colliders = new List<string>(); // world collider names to collide against
    }

    [Serializable]
    public class JiggleConfig
    {
        public JiggleSettings settings = new JiggleSettings();
        public List<JiggleColliderConfig> colliders = new List<JiggleColliderConfig>();
        public List<JiggleBoneConfig> bones = new List<JiggleBoneConfig>();
    }
}
