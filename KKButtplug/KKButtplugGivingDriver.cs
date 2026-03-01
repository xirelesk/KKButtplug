using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using PenetrationTech;

public class KKButtplugGivingDriver : MonoBehaviour
{
    [Header("Refs")]
    public Kobold kobold;
    public KKButtplug buttplug;

    [Header("Output")]
    [Range(0f, 1f)] public float maxVibration = 0.70f;
    [Range(0f, 1f)] public float minVibrationWhenNegativeStimulation = 0.10f;

    private int _active = 0;
    private readonly HashSet<Penetrator> _subs = new HashSet<Penetrator>();

    private const string SourceId = "giving";

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        _active = 0;
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

        foreach (var ds in kobold.activeDicks)
        {
            if (ds?.dick == null) continue;

            var p = ds.dick;
            if (_subs.Contains(p)) continue;

            p.penetrationStart += OnStart;
            p.penetrationEnd += OnEnd;
            _subs.Add(p);
        }
    }

    private void Unsubscribe()
    {
        foreach (var p in _subs)
        {
            if (p == null) continue;
            p.penetrationStart -= OnStart;
            p.penetrationEnd -= OnEnd;
        }
        _subs.Clear();
    }

    private void OnStart(Penetrable hole)
    {
        if (!IsLocal()) return;
        _active++;
    }

    private void OnEnd(Penetrable hole)
    {
        if (!IsLocal()) return;
        _active = Mathf.Max(0, _active - 1);
    }

    private void Update()
    {
        if (kobold == null || buttplug == null) return;
        if (!IsLocal()) return;

        // Keep up with dicks being added later (async equip)
        Subscribe();

        if (_active <= 0)
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
