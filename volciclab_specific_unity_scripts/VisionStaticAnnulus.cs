/*
 * VisionStaticAnnulus
 *
 * A visual-field stimulus object: a flat annulus (a filled disc when innerDiameter == 0) whose
 * position and size are defined in degrees of visual angle at the eye. It is attached to an
 * empty child GameObject of one of the two anchor pivots under CenterEyeAnchor:
 *   - 'Scotoma'              under GazeContingentGameObject   (gaze-contingent; follows the gaze)
 *   - 'EyeTrackerCalibrator' under HeadContingentGameObject   (head-contingent; fixed to the head)
 *
 * The same script is deliberately attached to several GameObjects at once. Each instance owns
 * all of its state; there are NO static mutable members in this class. (The lesson that made
 * this rule: StreamRigidBodyFromOptitrack, before the fix, shared one `static` receive buffer between
 * all instances, so N interleave-streamed objects only ever updated round-robin. Per-object
 * data must live in the component instance.)
 *
 * Geometry (tangent-plane convention, perimetry-standard):
 *   D      = eye (CenterEyeAnchor) to anchor-pivot distance, in metres. Read live, every
 *            frame, as the parent's localPosition.MAGNITUDE — not .z: the gaze pivot's
 *            localPosition is re-rotated every frame by GazeContingentTransform2, which
 *            shrinks its .z by cos(gaze); the magnitude does not.
 *   x, y   = D * tan(azimuth), D * tan(elevation)   — this object's LOCAL position (z stays 0)
 *   r_out  = D * tan(outerDiameter / 2)             — this object's LOCAL x/y scale
 *   r_in   = D * tan(innerDiameter  / 2)            — baked into the mesh as the ratio r_in/r_out
 *   Angles: azimuth +x = right, elevation +y = up, dead-center-ahead = (0, 0).
 *           (Same convention as the eye tracker's CurrentGazeVector.)
 *
 * Facing: in an anchor-pivot's local frame the eye sits exactly at (0, 0, -D), so the
 * stimulus plane (local XY) is perpendicular to the eye→stimulus line by construction.
 * No per-frame LookAt is needed. The eye is on the -Z side of the plane, so the material
 * is Cull Off (double-sided) — a front-culled annulus would face the eye the wrong way.
 *
 * Visibility: the base material uses the Meta XR SDK's own "Unlit/Crosshair" shader —
 * chosen deliberately: Meta's own shaders are the proven-reliable baseline on Quest
 * hardware (a project-written shader with the same intended pass state silently failed
 * on two device test runs; field experience predates us on the Go generation).
 * Its pass declares Cull Off + ZTest Always + ZWrite Off, i.e. "renders over
 * everything" regardless of depth. renderQueue (default 3001, the "overlay" band)
 * orders the instances against each other — keep it unique between
 * simultaneously-visible instances.
 *
 * COLOR CONTRACT — read before touching _Color: Unlit/Crosshair's fragment is
 *     out = texture * vertexColor * _Color * 2.0
 * With the default white texture, the exact requested color C appears on screen iff
 * we store _Color = C / 2 AND the mesh vertex colors are white (1,1,1,1) — both are
 * enforced below (and why the mesh builder pins white vertex colors). Alpha works
 * out to 1 (1 * 1 * 0.5 * 2). Laboratory calibration colors are ABSOLUTE, so this
 * divisor must stay documented every time this script changes.
 * VERIFIED ON DEVICE 2026-10-28 — requested colors matched the lab's calibrated
 * references with the disc and the white vertex pin in place.
 *
 * BUILD INCLUSION: the base material ASSET lives at
 *     Assets\Resources\Stimulus\VisionStaticAnnulus.mat
 * — Resources content is always included in the build, and a material drags the
 * shader it references along with it. That is what makes this work without
 * Shader.Find (whose string only matches shaders actually present in the build)
 * and without any Graphics-settings registration. DO NOT move the material out
 * of the Resources folder. The lab alternative shader remains at
 * Assets\Shaders\VisionStaticAnnulus.shader (not the default; it is what failed
 * on-device twice).
 *
 * Lighting: none — an unlit solid color. No lights, no ambient, no GI, no reflections,
 * by construction (the shader).
 *
 * Color: canonical form is HTML hex (default "#808080" = 50% grey). The serialized field
 * is a Unity Color (the Inspector color slot accepts hex as well); SetColorHex() /
 * TryParseHexColor() are the hex entry points from code (and the natural payload format
 * for a future UDP control port).
 *
 * Runtime depth (port 40004, ExternalScotomaDistanceAdjuster): no coupling needed — because
 * pose and scale are recomputed every frame from the live D, a 40004 depth change applies
 * on the next frame. This script NEVER writes to the anchor pivots: their transforms are
 * owned by GazeContingentTransform2 (gaze) and the scene layout (head).
 *
 * Scene needs: the two empty child GameObjects above (attached by hand in the editor, with
 * per-instance settings — the calibrator and the scotoma use different parameters).
 *
 * Full design notes and the "why": sibling file VisionStaticAnnulus.cs.md.
 */
using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

public class VisionStaticAnnulus : MonoBehaviour
{
    /*
     * Parameters — one set PER GameObject. Attaching several instances of this script in
     * the same scene is safe: every state involved is owned by the instance it is attached
     * to. No statics anywhere in this class.
     */
    public bool renderEnabled = true;

    // Visual angle of the annulus center, degrees: azimuth (x), elevation (y).
    // +x = right, +y = up, dead-center-ahead = (0, 0). Same convention as the eye tracker.
    public Vector2 centerAngles = new Vector2(0, 0);

    // Inner diameter, degrees of visual angle. 0 => filled disc (no hole).
    public float innerDiameter = 0.1f;

    // Outer diameter, degrees of visual angle. Always kept >= innerDiameter (clamped).
    public float outerDiameter = 0.5f;

    // Stimulus color; canonical form is HTML hex (default "#808080" = 50% grey).
    // Inspector color slots accept hex too; from code use SetColorHex().
    public Color annulusColor = new Color(0.5f, 0.5f, 0.5f, 1f);

    // Render queue. 3001+ is the "overlay" band (drawn after all opaques). If two
    // instances overlap while simultaneously visible, keep this unique per instance.
    public int renderQueue = 3001;

    // Circle resolution (segment count of the annulus). Minimum 3.
    public int segments = 96;

    /*
     * DIAGNOSTICS — standing lab tool (default OFF; enable on the instance while
     * debugging on device; decode in .cs.md § "On-device troubleshooting").
     *
     * verboseDiagnostics: logs the full per-frame state (pivot + D, local pose,
     *   eye-relative world position, the filter/mesh/material hookup, and the live
     *   material's shader/_Color/queue) on the first frame and every diagnosticsPeriod
     *   frames. This is what caught the 2026-10-28 failure (the missing
     *   meshFilter.sharedMesh hookup showed up as filterMesh = NULL).
     * debugMaterialResourcesPath: base material override, by Resources path ("" = the
     *   default Stimulus/VisionStaticAnnulus). "Stimulus/DebugSolid" (URP/Lit, Cull Off,
     *   opaque constant color — the same shader as the scene's test_cube) is the known-
     *   good fallback for isolating "geometry/scene" from "crosshair pass" failures.
     *
     * Proven on device 2026-10-28 (after the mesh hookup fix): rendering,
     * ZTest Always (hand-occlusion test), and the x2 color contract.
     */
    public bool verboseDiagnostics = false;
    public int diagnosticsPeriod = 100;
    public string debugMaterialResourcesPath = "";

    /* ----- internal state (all per-instance) ----- */

    // Base material, in the project Resources folder (always in the build; see header).
    private const string resourceMaterialPath = "Stimulus/VisionStaticAnnulus";

    // Unlit/Crosshair multiplies the output by 2.0 (see header); we store C / 2 so C renders.
    private const float colorDivisor = 2f;

    // Disc CENTER position (azimuth/elevation): a genuine ceiling — a point at exactly 90° sits
    // straight off to the side, and beyond it the stimulus is behind the eye. This keeps tan()
    // finite and the center in front of the eye. Keep this at 89.99° (do NOT raise it).
    private const float maxCenterAngle = 89.99f;
    // Disc OUTER EDGE (diameter): a FLAT tangent-plane disc's radius is capped at 89.9° of visual
    // angle, i.e. a DIAMETER of ~179.8°. The single old constant clamped the DIAMETER to 89.99°
    // (radius ~45° — below the headset's ~55° half-FOV). Raised to 170° (radius 85°) so the flat
    // disc can span past the FOV — the inverse-scotoma / tunnel-vision case (a round foveal disc
    // + a solid-colour field whose edge sits beyond the visible field, so its boundary is unseen).
    private const float maxDiameter = 170f;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Material stimulusMaterial;   // per-instance; NEVER shared with another object
    private Mesh annulusMesh;            // per-instance; NEVER shared with another object

    // What the mesh was last built for, so it is rebuilt only when the shape actually changes.
    private float builtInnerDiameter = float.NaN;
    private float builtOuterDiameter = float.NaN;
    private int builtSegments = -1;

    // What the material was last told, so we do not rewrite it every frame. The initial
    // appliedColor carries NaN components, and NaN never compares equal, so the first
    // application always happens.
    private Color appliedColor = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
    private int appliedQueue = int.MinValue;

    // One-shot warnings (logged once per changed input, not once per frame).
    private bool depthZeroWarned = false;
    private string lastClampWarning = string.Empty;

    // Diagnostics: cached eye anchor for eye-relative positions; Update() frame counter.
    private Transform eyeAnchor;
    private int diagnosticFrame = 0;

    void Start()
    {
        // en-US culture. Required for the decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Self-bootstrapping: the target GameObjects are empty, so the script owns its own
        // render plumbing (created here if missing).
        meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = gameObject.AddComponent<MeshFilter>();
        }

        meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer == null)
        {
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
        }

        annulusMesh = new Mesh();
        annulusMesh.name = gameObject.name + " annulus";

        // RENDER HOOKUP — the MeshFilter must reference this mesh or the MeshRenderer has
        // nothing to draw. This link is SILENT when missing (no log, no error, everything
        // else looks correct) — it was THE on-device failure of 2026-10-28. LogDiagnostics
        // reports filterMesh too, so a broken hookup is now visible, not guessable.
        meshFilter.sharedMesh = annulusMesh;

        // Base material from the project Resources folder — always included in the build,
        // and it drags the shader it references along with it. This is why no
        // Shader.Find / Graphics-settings registration is needed.
        // debugMaterialResourcesPath ("") selects the base — see the Diagnostics block.
        string basePath = string.IsNullOrEmpty(debugMaterialResourcesPath)
            ? resourceMaterialPath
            : debugMaterialResourcesPath;

        Material baseMaterial = Resources.Load<Material>(basePath);
        if (baseMaterial == null)
        {
            Debug.LogError("[VisionStaticAnnulus] (" + gameObject.name + ") base material not found at Resources " +
                "path '" + basePath + "' — the stimulus will not render. Check that " +
                "Assets\\Resources\\" + basePath + ".mat exists and is still inside Resources.");
            return;
        }
        if (baseMaterial.shader == null)
        {
            Debug.LogError("[VisionStaticAnnulus] (" + gameObject.name + ") the base material '" + basePath +
                "' has no valid shader reference — check the material in the editor and that the package " +
                "providing its shader is present.");
            return;
        }
        Debug.Log("[VisionStaticAnnulus] (" + gameObject.name + ") base material '" + basePath +
            "' OK; shader = '" + baseMaterial.shader.name + "'.");

        // Diagnostics: cache the eye anchor (for eye-relative positions in the log).
        GameObject eyeAnchorObject = GameObject.Find("CenterEyeAnchor");
        eyeAnchor = eyeAnchorObject != null ? eyeAnchorObject.transform : null;

        // A material PER INSTANCE. A shared material here would leak writes into every
        // other object using it (the classic shared-state bug — see the .cs.md).
        stimulusMaterial = new Material(baseMaterial);
        stimulusMaterial.name = gameObject.name + " annulus";
        meshRenderer.sharedMaterial = stimulusMaterial;

        // Note: the mesh geometry itself is built lazily on the first Update() pass,
        // after the parameter sanitization has run.
    }

    void Update()
    {
        if (annulusMesh == null || stimulusMaterial == null)
        {
            return;   // before the first frame, or Start() could not load the base material.
        }

        ApplyVisualState();

        // renderEnabled: toggle the renderer (and skip the pose work while hidden).
        if (renderEnabled)
        {
            if (!meshRenderer.enabled)
            {
                meshRenderer.enabled = true;
            }
        }
        else
        {
            if (meshRenderer.enabled)
            {
                meshRenderer.enabled = false;
            }
            return;
        }

        if (transform.parent == null)
        {
            Debug.LogError("[VisionStaticAnnulus] (" + gameObject.name + ") must be a child of one of the " +
                "anchor GameObjects (GazeContingentGameObject or HeadContingentGameObject) — it has no " +
                "parent, so no depth can be derived and no visual angle defined.");
            return;
        }

        // --- Geometry: rebuild the mesh only when the inner/outer ratio changed. ---
        // Sanitize: non-finite input from anywhere collapses to a finite value instead of
        // propagating NaN into transforms (Unity would error-spam and cull the object).
        float rawInner = innerDiameter;
        float rawOuter = outerDiameter;
        float inner = Finite(rawInner, 0f, 0f, maxDiameter);
        float outer = Finite(rawOuter, inner, 0f, maxDiameter);
        outer = Mathf.Max(inner, outer);

        if (IsOutOfRange(rawInner, maxDiameter) || IsOutOfRange(rawOuter, maxDiameter))
        {
            string signature = rawInner + "|" + rawOuter;
            if (signature != lastClampWarning)
            {
                Debug.LogWarning("[VisionStaticAnnulus] (" + gameObject.name + ") diameters out of the valid " +
                    "range (0.." + maxDiameter.ToString("0.##", CultureInfo.InvariantCulture) + "°, outer >= inner) — clamped to inner=" +
                    inner.ToString("0.###", CultureInfo.InvariantCulture) + "°, outer=" +
                    outer.ToString("0.###", CultureInfo.InvariantCulture) + "°.");
                lastClampWarning = signature;
            }
        }

        int n = Mathf.Max(3, segments);
        if (inner != builtInnerDiameter || outer != builtOuterDiameter || n != builtSegments)
        {
            RebuildAnnulus(inner, outer, n);
            builtInnerDiameter = inner;
            builtOuterDiameter = outer;
            builtSegments = n;
        }

        // --- Pose and scale, from the LIVE anchor depth. ---
        // D = magnitude, NOT .z: the gaze pivot's offset is re-rotated every frame by
        // GazeContingentTransform2, which shrinks localPosition.z by cos(gaze). The
        // magnitude stays exactly the authored depth (rotations preserve length) — and
        // this is what port 40004 sets. Reading it live is what makes 40004 "just work".
        float depth = transform.parent.localPosition.magnitude;
        if (depth <= Mathf.Epsilon)
        {
            if (!depthZeroWarned)
            {
                Debug.LogWarning("[VisionStaticAnnulus] (" + gameObject.name + ") anchor depth is ~0 — the " +
                    "stimulus collapses into a point at the eye (was 40004 set to 0?).");
                depthZeroWarned = true;
            }
            transform.localPosition = Vector3.zero;
            transform.localScale = Vector3.zero;
            return;
        }
        depthZeroWarned = false;

        float azRaw = centerAngles.x;
        float elRaw = centerAngles.y;
        float az = Finite(azRaw, 0f, -maxCenterAngle, maxCenterAngle);
        float el = Finite(elRaw, 0f, -maxCenterAngle, maxCenterAngle);

        if (IsOutOfRange(azRaw, maxCenterAngle) || IsOutOfRange(elRaw, maxCenterAngle))
        {
            string signature = "az|el|" + azRaw + "|" + elRaw;
            if (signature != lastClampWarning)
            {
                Debug.LogWarning("[VisionStaticAnnulus] (" + gameObject.name + ") centerAngles out of the valid " +
                    "range (±" + maxCenterAngle.ToString("0.##", CultureInfo.InvariantCulture) + "°) — clamped to [" +
                    az.ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                    el.ToString("0.###", CultureInfo.InvariantCulture) + "].");
                lastClampWarning = signature;
            }
        }

        // Tangent-plane placement: (x, y) = D·tan(angle); local z stays 0 (the anchor
        // pivot's depth IS the stimulus depth — the script never writes the pivot).
        transform.localPosition = new Vector3(depth * Mathf.Tan(Mathf.Deg2Rad * az), depth * Mathf.Tan(Mathf.Deg2Rad * el), 0f);

        // The mesh is unit-outer-radius; outer radius drives the x/y scale. The inner/outer
        // ratio is baked into the mesh (above), so a depth change only ever re-scales.
        float outerRadius = depth * Mathf.Tan(Mathf.Deg2Rad * outer * 0.5f);
        transform.localScale = new Vector3(outerRadius, outerRadius, 1f);

        // Diagnostics (first frame, then every diagnosticsPeriod — see .cs.md).
        diagnosticFrame++;
        if (verboseDiagnostics && (diagnosticFrame == 1 || diagnosticFrame % Mathf.Max(1, diagnosticsPeriod) == 0))
        {
            LogDiagnostics();
        }
    }

    void OnDestroy()
    {
        if (stimulusMaterial != null)
        {
            Destroy(stimulusMaterial);
        }
        if (annulusMesh != null)
        {
            Destroy(annulusMesh);
        }
    }

    /*
     * Push the color and render queue into the (per-instance) material, only when they
     * actually changed.
     */
    void ApplyVisualState()
    {
        // NaN components in the initial appliedColor never compare equal — first apply guaranteed.
        // COLOR CONTRACT: Unlit/Crosshair multiplies the output by 2.0, so we store C / 2
        // for the on-screen color to be exactly C (with the white texture and white vertex
        // colors — see the header). Track the REQUESTED color, not the stored one.
        if (annulusColor != appliedColor)
        {
            Color stored = new Color(
                annulusColor.r / colorDivisor,
                annulusColor.g / colorDivisor,
                annulusColor.b / colorDivisor,
                annulusColor.a / colorDivisor);
            stimulusMaterial.SetColor("_Color", stored);
            appliedColor = annulusColor;
        }

        int queue = Mathf.Clamp(renderQueue, 1000, 5000);
        if (queue != appliedQueue)
        {
            if (queue != renderQueue)
            {
                Debug.LogWarning("[VisionStaticAnnulus] (" + gameObject.name + ") renderQueue " + renderQueue +
                    " clamped to " + queue + " (Unity's valid range is 1000..5000).");
            }
            stimulusMaterial.renderQueue = queue;
            appliedQueue = queue;
        }
    }

    /*
     * Color as HTML hex: "#RGB", "#RRGGBB" or "#RRGGBBAA" (the leading '#' is optional).
     * Hex is the canonical form: the lab's calibration colors are the headset's calibrated
     * inner-screen colors, and hex is how those are written down (50% grey = "#808080").
     */
    public bool SetColorHex(string hex)
    {
        if (TryParseHexColor(hex, out Color color))
        {
            annulusColor = color;
            return true;
        }
        return false;
    }

    public static bool TryParseHexColor(string hex, out Color color)
    {
        color = Color.black;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string value = hex.Trim();
        if (value.StartsWith("#"))
        {
            value = value.Substring(1);
        }

        int red, green, blue, alpha;

        if (value.Length == 3)
        {
            int r = HexNibble(value[0]);
            int g = HexNibble(value[1]);
            int b = HexNibble(value[2]);
            if (r < 0 || g < 0 || b < 0)
            {
                return false;
            }
            red = (r << 4) | r;
            green = (g << 4) | g;
            blue = (b << 4) | b;
            alpha = 255;
        }
        else if (value.Length == 6 || value.Length == 8)
        {
            if (!HexByte(value, 0, out red) || !HexByte(value, 2, out green) || !HexByte(value, 4, out blue))
            {
                return false;
            }
            if (value.Length == 8)
            {
                if (!HexByte(value, 6, out alpha))
                {
                    return false;
                }
            }
            else
            {
                alpha = 255;
            }
        }
        else
        {
            return false;
        }

        color = new Color(red / 255f, green / 255f, blue / 255f, alpha / 255f);
        return true;
    }

    static bool HexByte(string value, int offset, out int number)
    {
        return int.TryParse(value.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)
            && number >= 0 && number <= 255;
    }

    static int HexNibble(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        return -1;
    }

    /*
     * Build the annulus (or filled disc, if the inner ratio collapses to 0) at UNIT outer
     * radius in the local XY plane. The inner/outer ratio is baked into the mesh; runtime
     * size comes entirely from the transform scale, so a depth change (40004) only ever
     * re-scales — nothing is regenerated per frame.
     */
    void RebuildAnnulus(float innerDeg, float outerDeg, int n)
    {
        // Bake the inner as the true tangent ratio r_in / r_out = tan(inner/2) / tan(outer/2) —
        // NOT the linear inner/outer. The whole mesh is scaled by D·tan(outer/2), so a linear ratio
        // places the inner edge at (inner/outer)·D·tan(outer/2) instead of the intended D·tan(inner/2);
        // the hole then GROWS with the outer (the artifact at large outer diameters). With tan(), both
        // edges land at their true visual angles. Matches the header intent ("baked in as r_in / r_out").
        float ratio = (outerDeg > 0f)
            ? Mathf.Tan(Mathf.Deg2Rad * innerDeg * 0.5f) / Mathf.Tan(Mathf.Deg2Rad * outerDeg * 0.5f)
            : 0f;
        // Floor the low end as well: innerDiameter == 0 (the "filled disc") used to take the FAN
        // branch (ratio == 0), which corrupts the shared mesh on the ring -> disc transition —
        // mesh.vertices is shrunk while the old, larger mesh.triangles is still attached, leaving
        // stale indices (the "disc gets smaller and a vertex pokes out" artifact). Flooring to a
        // sub-pixel hole keeps it structurally in the clean ring branch. Verified on-device:
        // inner = 0 -> mangled; inner ~ 0.0001 -> clean.
        ratio = Mathf.Clamp(ratio, 0.0001f, 0.999f);   // inner == outer is still (nearly) a zero-area ring

        Vector3[] normals;
        Vector3[] vertices;
        int[] triangles;

        if (ratio > 0f)
        {
            // Ring: inner and outer loops of n vertices each.
            vertices = new Vector3[2 * n];
            normals = new Vector3[2 * n];
            triangles = new int[n * 6];

            for (int i = 0; i < n; i++)
            {
                float angle = 2f * Mathf.PI * i / n;
                float c = Mathf.Cos(angle);
                float s = Mathf.Sin(angle);

                vertices[i] = new Vector3(c * ratio, s * ratio, 0f);    // inner ring
                vertices[i + n] = new Vector3(c, s, 0f);                // outer ring (unit radius)
                normals[i] = new Vector3(0f, 0f, 1f);
                normals[i + n] = new Vector3(0f, 0f, 1f);
            }

            for (int i = 0; i < n; i++)
            {
                int a = i;                  // inner_i
                int b = i + n;              // outer_i
                int c2 = (i + 1) % n;       // inner_(i+1)
                int d = c2 + n;             // outer_(i+1)

                // Two CCW triangles (from the +Z side) per segment. Winding is cosmetic
                // here (Cull Off), but it should be sane.
                triangles[i * 6 + 0] = a;
                triangles[i * 6 + 1] = b;
                triangles[i * 6 + 2] = d;
                triangles[i * 6 + 3] = a;
                triangles[i * 6 + 4] = d;
                triangles[i * 6 + 5] = c2;
            }
        }
        else
        {
            // Filled disc: center vertex + outer loop, fan-triangulated.
            vertices = new Vector3[n + 1];
            normals = new Vector3[n + 1];
            triangles = new int[n * 3];

            vertices[0] = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                float angle = 2f * Mathf.PI * i / n;
                vertices[1 + i] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
            }
            for (int i = 0; i <= n; i++)
            {
                normals[i] = new Vector3(0f, 0f, 1f);
            }

            for (int i = 0; i < n; i++)
            {
                triangles[i * 3 + 0] = 0;
                triangles[i * 3 + 1] = 1 + i;
                triangles[i * 3 + 2] = 1 + ((i + 1) % n);
            }
        }

        // Unlit/Crosshair multiplies in the vertex color (out = tex * vcolor * _Color * 2.0);
        // pin it to white so that the stored _Color = C/2 maps to exactly C on screen.
        Color[] colors = new Color[vertices.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = Color.white;
        }

        annulusMesh.vertices = vertices;
        annulusMesh.normals = normals;
        annulusMesh.triangles = triangles;
        annulusMesh.colors = colors;
        annulusMesh.RecalculateBounds();
    }

    // Clamp to [min, max], routing non-finite input to the fallback.
    static float Finite(float value, float fallback, float min, float max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return Mathf.Clamp(fallback, min, max);
        }
        return Mathf.Clamp(value, min, max);
    }

    static bool IsOutOfRange(float value, float maxAbs)
    {
        return float.IsNaN(value) || float.IsInfinity(value) || value < -maxAbs || value > maxAbs;
    }

    /*
     * Diagnostics (verboseDiagnostics): a compact dump of everything the "is it in front
     * of the eye and actually rendering" question hinges on, logged in the exact state
     * that was just applied this frame. Decode table: .cs.md § "On-device troubleshooting".
     *
     * Key facts per line:
     *   line 1 — where the pivot is (D), and where/what-scale we wrote ourselves.
     *   line 2 — world position vs the eye; inFront > 0 ⇒ in front of the eye along the
     *             gaze axis (inFront IS the signed distance ahead on that axis).
     *   line 3 — renderer on? mesh non-empty? extents sane? which material is live, and
     *             with which shader? (name+shader of the LIVE material = ground truth)
     *   line 4 — the requested color vs what is actually stored in the material (this is
     *             where a broken C/2 contract or a dead material would show up).
     */
    void LogDiagnostics()
    {
        string n = gameObject.name;
        Transform pivot = transform.parent;
        float D = (pivot != null) ? pivot.localPosition.magnitude : -1f;

        Debug.Log("[VisionStaticAnnulus][dbg] (" + n + ") parent=" + (pivot != null ? pivot.name : "null") +
            " pivotPos=" + P(pivot != null ? pivot.localPosition : Vector3.zero) +
            " D=" + F(D) +
            " localPos=" + P(transform.localPosition) +
            " localScale=" + P(transform.localScale));

        Transform eye = eyeAnchor != null ? eyeAnchor : (pivot != null ? pivot.parent : null);
        if (eye != null)
        {
            Vector3 eyePos = eye.position;
            Vector3 toStimulus = transform.position - eyePos;
            Vector3 gazeAxis = (pivot != null) ? (pivot.position - eyePos) : Vector3.forward;
            float inFront = (gazeAxis.sqrMagnitude > 1e-12f)
                ? Vector3.Dot(toStimulus, gazeAxis) / Mathf.Max(1e-6f, gazeAxis.magnitude)
                : -1f;
            Debug.Log("[VisionStaticAnnulus][dbg] (" + n + ") eye=" + P(eyePos) +
                " worldPos=" + P(transform.position) +
                " distToEye=" + F(toStimulus.magnitude) +
                " inFront=" + F(inFront) +
                "   (inFront>0: in front of eye; ≈ D and distToEye when centered)");
        }
        else
        {
            Debug.Log("[VisionStaticAnnulus][dbg] (" + n + ") eye anchor not found — skipping the eye-relative line (check the hierarchy: pivot.parent)");
        }

        if (annulusMesh != null && meshRenderer != null)
        {
            Debug.Log("[VisionStaticAnnulus][dbg] (" + n + ") renderer.enabled=" + meshRenderer.enabled +
                " liveMaterial=" + (meshRenderer.sharedMaterial != null
                    ? meshRenderer.sharedMaterial.name + " (shader: " + meshRenderer.sharedMaterial.shader.name + ")"
                    : "null") +
                " filterMesh=" + (meshFilter != null && meshFilter.sharedMesh != null
                    ? meshFilter.sharedMesh.name + " (" + meshFilter.sharedMesh.vertexCount + " verts)"
                    : "NULL — the renderer has no mesh attached and draws nothing") +
                " mesh.tris=" + (annulusMesh.triangles.Length / 3) +
                " meshExtents=" + P(annulusMesh.bounds.extents));
        }

        if (stimulusMaterial != null)
        {
            Color stored = stimulusMaterial.GetColor("_Color");
            Debug.Log("[VisionStaticAnnulus][dbg] (" + n + ") requestedColor=(" + F(annulusColor.r) + "," + F(annulusColor.g) + "," +
                F(annulusColor.b) + ",a=" + F(annulusColor.a) +
                ") stored_Color=(" + F(stored.r) + "," + F(stored.g) + "," + F(stored.b) + ",a=" + F(stored.a) +
                ") renderQueue=" + stimulusMaterial.renderQueue);
        }
    }

    // en-US decimal formatting for the diagnostics log (culture is pinned in Start()).
    static string F(float v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    static string P(Vector3 v)
    {
        return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
    }
}
