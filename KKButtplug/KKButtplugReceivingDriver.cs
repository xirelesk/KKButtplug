using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using PenetrationTech;

public class KKButtplugReceivingDriver : MonoBehaviour
{
    [Header("Refs")]
    public Kobold kobold;
    public KKButtplug buttplug;

    [Header("Receiving Detection")]
    [Tooltip("Seconds to keep receiving active after last valid penetration update.")]
    public float graceSeconds = 0.25f;

    [Tooltip("Require this much depth past the penetrable's actual hole start (world units).")]
    public float minDepthPastHoleStartWorld = 0.05f;

    private float _lastValidReceiveTime = -999f;
    private readonly List<Penetrable> _subs = new List<Penetrable>();

    private const string SourceId = "receiving";

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        if (buttplug != null)
            buttplug.ClearSource(SourceId);
    }

    private bool IsLocal()
    {
        var pv = kobold != null ? kobold.GetComponent<PhotonView>() : null;
        return pv != null && pv.IsMine;
    }

    private void Subscribe()
    {
        if (kobold == null) return;

        foreach (var set in kobold.penetratables)
        {
            if (set?.penetratable == null) continue;
            if (_subs.Contains(set.penetratable)) continue;

            set.penetratable.penetrationNotify += OnPenetrationNotify;
            _subs.Add(set.penetratable);
        }
    }

    private void Unsubscribe()
    {
        foreach (var p in _subs)
        {
            if (p == null) continue;
            p.penetrationNotify -= OnPenetrationNotify;
        }
        _subs.Clear();
    }

    private void OnPenetrationNotify(
        Penetrable penetrable,
        Penetrator penetrator,
        float worldDistanceToPenetratorRoot,
        Penetrable.SetClipDistanceAction clipAction)
    {
        if (!IsLocal()) return;
        if (penetrable == null || penetrator == null) return;

        // Ensure penetrator is actually inserted into THIS penetrable
        if (!penetrator.TryGetPenetrable(out var currentHole) || currentHole != penetrable)
            return;

        float holeStart = penetrable.GetActualHoleDistanceFromStartOfSpline();

        if (worldDistanceToPenetratorRoot >= holeStart + minDepthPastHoleStartWorld)
            _lastValidReceiveTime = Time.unscaledTime;
    }

    private void Update()
    {
        if (kobold == null || buttplug == null) return;
        if (!IsLocal()) return;

        // Re-check in case penetratables populate late
        if (_subs.Count == 0 && kobold.penetratables.Count > 0)
            Subscribe();

        bool receiving = (Time.unscaledTime - _lastValidReceiveTime) <= graceSeconds;

        if (!receiving)
        {
            buttplug.SetSourceVibration(SourceId, 0f);
            return;
        }

        float target = 0f;

        if (kobold.stimulation < 0f)
        {
            // Below zero → minimum vibration (while receiving only)
            target = KKButtplug.MinVibration.Value;
        }
        else if (kobold.stimulationMax > 0.001f)
        {
            float normalized = kobold.stimulation / kobold.stimulationMax;
            normalized = Mathf.Clamp01(normalized);

            target = Mathf.Lerp(
                KKButtplug.MinVibration.Value,
                KKButtplug.MaxVibration.Value,
                normalized
            );
        }

        buttplug.SetSourceVibration(SourceId, Mathf.Clamp01(target));
    }
}