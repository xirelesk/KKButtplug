using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using PenetrationTech;

public class KKButtplugGivingDriver : MonoBehaviour
{
    [Header("Refs")]
    public Kobold kobold;
    public KKButtplug buttplug;

    private int _active = 0;
    private readonly HashSet<Penetrator> _subs = new HashSet<Penetrator>();

    // Throttled resubscribe for async equips / different prefab layouts
    private float _resubTimer = 0f;

    private const string SourceId = "giving";

    private void OnEnable()
    {
        _resubTimer = 0f;
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        _active = 0;

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
        if (kobold == null || kobold.activeDicks == null)
            return;

        foreach (var ds in kobold.activeDicks)
        {
            if (ds?.descriptor == null)
                continue;

            // Search the entire dick prefab hierarchy (penetrator location varies per dick type)
            var penetrators = ds.descriptor.GetComponentsInChildren<Penetrator>(true);

            foreach (var p in penetrators)
            {
                if (p == null) continue;
                if (_subs.Contains(p)) continue;

                p.penetrationStart += OnStart;
                p.penetrationEnd += OnEnd;
                _subs.Add(p);
            }
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

        // Periodically rescan for penetrators (covers async equip + multi-penetrator prefabs)
        _resubTimer -= Time.unscaledDeltaTime;
        if (_resubTimer <= 0f)
        {
            _resubTimer = 0.5f;
            Subscribe();
        }

        if (_active <= 0)
        {
            buttplug.SetSourceVibration(SourceId, 0f);
            return;
        }

        float target = 0f;

        if (kobold.stimulation < 0f)
        {
            // Below zero → minimum vibration (while giving only)
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