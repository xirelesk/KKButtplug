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

    [Header("Output")]
    [Range(0f, 1f)] public float maxVibration = 0.70f;
    [Range(0f, 1f)] public float minVibrationWhenNegativeStimulation = 0.10f;

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
        if (buttplug != null) buttplug.ClearSource(SourceId);
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

        // Gate: only count if penetrator actually reports it is inserted into THIS penetrable.
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

        // In case penetrables were populated later (async), keep subscriptions fresh
        if (_subs.Count == 0) Subscribe();

        bool receiving = (Time.unscaledTime - _lastValidReceiveTime) <= graceSeconds;

        if (!receiving)
        {
            buttplug.SetSourceVibration(SourceId, 0f);
            return;
        }

        float target;
        if (kobold.stimulation < 0f)
            target = minVibrationWhenNegativeStimulation;
        else if (kobold.stimulationMax > 0.001f)
            target = (kobold.stimulation / kobold.stimulationMax) * maxVibration;
        else
            target = 0f;

        buttplug.SetSourceVibration(SourceId, Mathf.Clamp01(target));
    }
}
