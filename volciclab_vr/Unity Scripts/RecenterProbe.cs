/*
 * RecenterProbe.cs — DIAGNOSTIC, throwaway by design. Delete this file if unneeded.
 *
 * The question it answers (empirically, on device):
 *   On THIS project's tracking origin ('Eye Level' / device space), can we
 *   (a) TRIGGER a recenter of the headset tracking from app code, AND
 *   (b) DETECT when a recenter happens (even if the PARTICIPANT does it)?
 *
 * Why it exists: the streaming-server worlds must stay 'married' (see
 * UnityOptitrackAlignment.cs). The alignment script reacts to the Meta XR SDK's
 * RecenteredPose EVENT (the detection half), and the old universe_relocator.cs tried
 * to TRIGGER a recenter via XRInputSubsystem.TryRecenter() on Meta XR SDK 83 — and
 * it did not visibly work. SDK 207.0.1's own OVRManager still routes RecenterPose()
 * through the same TryRecenter(), and Unity's docs say recentering activates
 * 'when using floor-based tracking origin' — Eye Level is not floor-based, so
 * it may be a no-op here. This probe settles it with evidence.
 *
 * Verified from source (Meta XR SDK 207.0.1):
 *   - OVRManager.display.RecenterPose() -> inputSubsystem.TryRecenter()   (OVRDisplay.cs)
 *   - RecenteredPose event is driven by OVRPlugin.GetLocalTrackingSpaceRecenterCount()
 *     polling in OVRDisplay.Update() — Quest-standalone only, and it also fires one
 *     frame after our OWN RecenterPose() call (the recenterRequested latch).
 *   - OVRManager's boot path: if (AllowRecenter && OVRPlugin.shouldRecenter) -> RecenterPose()
 *
 * Usage (main-thread, one-shot):
 *   - Attach to any GameObject (it renders nothing).
 *   - Flip 'triggerProbe' to true (Inspector via MQDH, or from code) — it runs once and
 *     latches itself back to false.
 *   - READ THE LOGS (search [RecenterProbe]).
 *   - Separately, to test the DETECTION half without app triggering: have a person
 *     reset the tracking through the headset's own system UI (Settings > reset
 *     tracking) and watch for the RECENTER-EVENT-FIRED line — and for the
 *     alignment script's '[UnityOptitrackAlignment] RecenteredPose event received' line.
 *
 * NOTE: if the TRIGGER half works, it moves the tracking origin — deliberately.
 * The alignment script (if on) will re-marry the worlds on the resulting event.
 */

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.OpenXR;

public class RecenterProbe : MonoBehaviour
{
    // Flip to true to run the probe once (it resets itself).
    public bool triggerProbe = false;

    // What the probe reads to decide 'did it move'.
    public string centerEyeAnchorName = "CenterEyeAnchor";
    public float settleSeconds = 1.0f;

    private bool probeRunning;
    private int recenterEventsFired;

    void Update()
    {
        if (triggerProbe && !probeRunning)
        {
            triggerProbe = false;
            probeRunning = true;
            StartCoroutine(Probe());
        }
    }

    IEnumerator Probe()
    {
        recenterEventsFired = 0;

        // (0) What the runtime THINKS it will permit.
        bool allowRecentering = false;
        try
        {
            allowRecentering = OpenXRSettings.AllowRecentering;
            Debug.Log("[RecenterProbe] OpenXRSettings.AllowRecentering = " + allowRecentering);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RecenterProbe] Could not read OpenXRSettings.AllowRecentering: " + e.Message);
        }

        // (1) Baseline: recenter count + eye-anchor pose.
        int countBefore = SafeRecenterCount();
        Transform anchor = FindAnchor();
        Vector3 positionBefore = anchor != null ? anchor.position : Vector3.zero;
        float yawBefore = anchor != null ? anchor.eulerAngles.y : 0f;
        Debug.Log("[RecenterProbe] Baseline: recenterCount=" + countBefore +
                  (anchor != null ? string.Format(", anchor at {0} (yaw {1:0.00}°)", positionBefore, yawBefore) : ", (no CenterEyeAnchor found)"));

        // (2) Listen for the SDK's recenter event.
        if (OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose += OnRecentered;
        }
        else
        {
            Debug.LogWarning("[RecenterProbe] OVRManager.display is null — cannot monitor the RecenteredPose event (on device it should exist).");
        }

        // (3) TRIGGER a recenter from app code.
        //     (Deliberately `var`: the XRInputSubsystem type lives in the XR subsystem
        //      namespaces whose exact location differs across Unity/SDK versions.)
        bool requested = false;
        try
        {
            var inputSubsystem = OVRManager.GetCurrentInputSubsystem();
            if (inputSubsystem != null)
            {
                requested = inputSubsystem.TryRecenter();
            }
            else
            {
                Debug.LogWarning("[RecenterProbe] No XRInputSubsystem found — nothing to recenter.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RecenterProbe] Recenter request threw: " + e.Message);
        }
        Debug.Log("[RecenterProbe] TryRecenter() returned: " + requested);

        // (4) Let the runtime settle, then measure.
        yield return new WaitForSeconds(settleSeconds);

        if (OVRManager.display != null)
        {
            OVRManager.display.RecenteredPose -= OnRecentered;
        }

        int countAfter = SafeRecenterCount();
        Vector3 positionAfter = anchor != null ? anchor.position : Vector3.zero;
        float yawAfter = anchor != null ? anchor.eulerAngles.y : 0f;

        Debug.Log("[RecenterProbe] After " + settleSeconds + " s: recenterCount=" + countAfter +
                  (anchor != null ? string.Format(", anchor at {0} (yaw {1:0.00}°)", positionAfter, yawAfter) : "") +
                  ", eventsFired=" + recenterEventsFired);

        bool evidenceOfRecenter = (countAfter != countBefore) || (recenterEventsFired > 0) ||
                                  (Vector3.Distance(positionBefore, positionAfter) > 0.0005f);
        if (evidenceOfRecenter)
        {
            Debug.Log("[RecenterProbe] VERDICT: recentering from app code WORKS on this tracking origin. The alignment script's auto-realign hook can therefore also be exercised on demand.");
        }
        else
        {
            Debug.Log("[RecenterProbe] VERDICT: recentering from app code did NOT take effect (TryRecenter=" + requested +
                      ", AllowRecentering=" + allowRecentering + "). Likely cause: the tracking origin is 'Eye Level' (device space), and recentering is a floor-based-origin feature. The DETECTION half (RecenteredPose on a participant-initiated reset) is worth testing separately via the system UI.");
        }

        probeRunning = false;
    }

    void OnRecentered()
    {
        recenterEventsFired++;
        Debug.Log("[RecenterProbe] RECENTER-EVENT-FIRED (count " + recenterEventsFired + ")");
    }

    Transform FindAnchor()
    {
        GameObject anchorObject = GameObject.Find(centerEyeAnchorName);
        return (anchorObject != null) ? anchorObject.transform : null;
    }

    int SafeRecenterCount()
    {
        try
        {
            return OVRPlugin.GetLocalTrackingSpaceRecenterCount();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RecenterProbe] GetLocalTrackingSpaceRecenterCount() unavailable: " + e.Message);
            return -1;
        }
    }
}
