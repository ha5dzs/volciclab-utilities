/*
 * Cranks the display refresh rate at startup and then keeps an eye on it.
 *
 * Context: the Quest Pro ships at 72 Hz; the panel offers 72 / 80 / 90 (90 Hz
 * is its maximum — there is no 120 Hz mode on the Pro; that is a Quest 2 / 3 /
 * 3S thing). For participant sessions we want the best rate the device
 * offers. The default `refreshRate` is 90 Hz; set `refreshRate = 0` to take
 * whatever the maximum ends up being on a future device, or any other
 * offered value.
 *
 * API (Meta XR SDK 207 + official doc "Set Display Refresh Rates"):
 *   OVRPlugin.systemDisplayFrequenciesAvailable  -> float[] of offered Hz
 *   OVRPlugin.systemDisplayFrequency             -> get/set the current Hz
 * Under this project's OpenXR pipeline these bridge to the
 * xrEnumerateDisplayRefreshRatesFB / xrRequestDisplayRefreshRateFB entry
 * points (MetaXRDisplayRefreshRateOVRPluginShim in the package cache).
 * The runtime REJECTS a rate that is not on the offered list, so we only
 * ever request a value that the list contains.
 *
 * Two on-device pitfalls learned the hard way (2026-11-04, both hit on the
 * first version of this script — do not "simplify" them back):
 *
 * 1. OVRManager.isHmdPresent is an XR SESSION NODE STATE (see
 *    OVRNodeStateProperties.IsHmdPresent in the package cache), and it is
 *    NOT settled at Awake() time — on the headset it read FALSE at the
 *    earliest possible moment and silently killed the first version. So we
 *    POLL for it, with a bounded budget, in Update().
 *
 * 2. The lab's Meta Quest Developer Hub log view was configured to hide
 *    Info-level logcat lines — Debug.Log lines never reached the user even
 *    though the script had executed them. The status markers this lab
 *    verifies against must therefore be visible under ANY log level setting:
 *    every outcome line below is at Debug.LogWarning. That is deliberate
 *    and is part of the contract, not an error-style convention.
 *
 * Confirmation is a READ-BACK poll of systemDisplayFrequency after the
 * request (the OVRManager.DisplayRefreshRateChanged event is NOT dispatched
 * under OpenXR per Meta's own docs, so it is not relied upon here).
 *
 * Throttling watch: per Meta's docs, a rate above 72 Hz can be dropped back
 * to 72 Hz by dynamic THERMAL throttling (first step: rate down; then frame
 * rate down). On a 1-hour participant session that would be a silent
 * experiment change, so after applying, this script re-reads the rate every
 * `watchThrottleEveryFrames` frames and logs whenever it CHANGES.
 *
 * Intended placement: the [BuildingBlock] Camera Rig (README step 47), or
 * any always-active object.
 */

using System.Globalization;
using UnityEngine;

public class CrankFrameRateUp : MonoBehaviour
{
    // Meddle in the editor.
    [Tooltip("The refresh rate to run at (Hz) — the way Quest itself names the setting. Pick a rate the device offers (Quest Pro: 72/80/90). " +
        "0 = take the maximum the device offers. If the rate itself is not offered, the closest offered rate is used (and it is logged).")]
    public float refreshRate = 90f;

    [Tooltip("Frames to wait for the HMD 'present' session state and the offered-frequency list to settle.")]
    public int waitBudgetFrames = 900;

    [Tooltip("After applying: re-read the rate every N frames and log any change (catches Meta's silent thermal 90->72 drops). 0 = off.")]
    public int watchThrottleEveryFrames = 900;

    private static readonly CultureInfo logCulture = CultureInfo.GetCultureInfo("en-US");

    // Frames after the request during which the read-back confirmation runs (every frame).
    private const int confirmationFrames = 120;

    private bool applied = false;
    private bool confirmDone = false;
    private float desiredHz = 0f;
    private float lastReportedHz = -1f;
    private int waitFrames = 0;
    private int confirmFrame = 0;
    private int watchTick = 0;

    private static string Hz(float f) { return f.ToString("0.###", logCulture) + "Hz"; }

    private static string List(float[] list)
    {
        string s = list[0].ToString("0.###", logCulture);
        for (int i = 1; i < list.Length; i++)
            s += ", " + list[i].ToString("0.###", logCulture);
        return s;
    }

    // Pick the offered rate to request: exact target if offered, else the closest offered value
    // (a tie goes to the higher rate — this lab optimises for smoothness over battery).
    private static float PickTarget(float[] available, float target)
    {
        if (target <= 0f)
        {
            float m = available[0];
            for (int i = 1; i < available.Length; i++)
                m = Mathf.Max(m, available[i]);
            return m;
        }

        float best = available[0];
        float bestDist = Mathf.Abs(available[0] - target);
        for (int i = 1; i < available.Length; i++)
        {
            float d = Mathf.Abs(available[i] - target);
            if (d < bestDist || (Mathf.Approximately(d, bestDist) && available[i] > best))
            {
                best = available[i];
                bestDist = d;
            }
        }
        return best;
    }

    private void Apply(float[] available, float requested)
    {
        float before = OVRPlugin.systemDisplayFrequency;
        OVRPlugin.systemDisplayFrequency = requested;
        applied = true;
        desiredHz = requested;
        lastReportedHz = requested;

        if (Mathf.Approximately(requested, refreshRate))
        {
            Debug.LogWarning("[CrankFrameRateUp] refreshRate " + Hz(refreshRate) +
                (Mathf.Approximately(before, requested)
                    ? " already in effect — nothing changed."
                    : " requested (was " + Hz(before) + ").") +
                " Offered by device: " + List(available) + ".");
        }
        else
        {
            Debug.LogWarning("[CrankFrameRateUp] refreshRate " + Hz(refreshRate) +
                " is NOT offered by this device; closest offered rate " + Hz(requested) +
                (Mathf.Approximately(before, requested) ? " already in effect." : " requested (was " + Hz(before) + ").") +
                " Offered by device: " + List(available) + ".");
        }
    }

    private void Update()
    {
        if (!applied)
        {
            if (++waitFrames > waitBudgetFrames)
            {
                bool hmd = OVRManager.isHmdPresent;
                Debug.LogWarning(hmd
                    ? "[CrankFrameRateUp] the offered-frequency list never returned a single rate within " + waitBudgetFrames + " frames — the runtime offered nothing. Skipping."
                    : "[CrankFrameRateUp] the HMD 'present' session state never settled within " + waitBudgetFrames + " frames (normal in the editor/PC; on a headset this is a startup problem). Skipping.");
                enabled = false;
                return;
            }

            // Pitfall 1 (see header): poll, do not assume.
            if (!OVRManager.isHmdPresent)
                return;

            float[] available = OVRPlugin.systemDisplayFrequenciesAvailable;
            if (available.Length == 0)
                return; // not settled yet; keep waiting

            Apply(available, PickTarget(available, refreshRate));
        }
    }

    // After applying, confirm by read-back, then watch for silent changes.
    private void LateUpdate()
    {
        if (!applied)
            return;

        if (!confirmDone)
        {
            int frame = ++confirmFrame;
            float now = OVRPlugin.systemDisplayFrequency;
            if (Mathf.Approximately(now, desiredHz))
            {
                confirmDone = true;
                Debug.LogWarning("[CrankFrameRateUp] confirmed: now running at " + Hz(now) + ".");
                return;
            }
            if (frame >= confirmationFrames)
            {
                confirmDone = true;
                lastReportedHz = now;
                Debug.LogWarning("[CrankFrameRateUp] requested " + Hz(desiredHz) +
                    " but the read-back still reports " + Hz(now) +
                    " — the request may have been REJECTED. Check that the build targets a device that supports this rate (README step 16) and the Meta doc 'Set Display Refresh Rates'.");
                return;
            }
            return;
        }

        if (watchThrottleEveryFrames > 0 && ++watchTick >= watchThrottleEveryFrames)
        {
            watchTick = 0;
            float now = OVRPlugin.systemDisplayFrequency;
            if (!Mathf.Approximately(now, lastReportedHz))
            {
                Debug.LogWarning("[CrankFrameRateUp] refresh rate changed " + Hz(lastReportedHz) + " -> " + Hz(now) +
                    " since the last check. Meta's dynamic throttling can silently drop a rate above 72 Hz back to 72 (heat/battery) — the app is still running, but check the headset temperature and the session state.");
                lastReportedHz = now;
            }
        }
    }
}
