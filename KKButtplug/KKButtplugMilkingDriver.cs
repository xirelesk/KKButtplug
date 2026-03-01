using UnityEngine;
using Photon.Pun;

public class KKButtplugMilkingDriver : MonoBehaviour
{
    public Kobold kobold;
    public KKButtplug buttplug;

    private const string SourceId = "milking";

    private int _pulsesRemaining = 0;
    private float _intervalTimer = 0f;
    private float _burstTimer = 0f;

    private bool _burstActive = false;

    private void OnEnable()
    {
        KKButtplugMilkingHooks.MilkingStarted += OnMilkingStarted;
    }

    private void OnDisable()
    {
        KKButtplugMilkingHooks.MilkingStarted -= OnMilkingStarted;

        if (buttplug != null)
            buttplug.ClearSource(SourceId);
    }

    private bool IsLocal()
    {
        var pv = kobold != null ? kobold.GetComponent<PhotonView>() : null;
        return pv != null && pv.IsMine;
    }

    private void OnMilkingStarted(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal())
            return;

        _pulsesRemaining = Mathf.Max(1, KKButtplug.MilkPulseCount.Value);

        _intervalTimer = 0f;   // fire first pulse immediately
        _burstTimer = 0f;
        _burstActive = false;
    }

    private void Update()
    {
        if (kobold == null || buttplug == null) return;
        if (!IsLocal()) return;

        if (_pulsesRemaining <= 0)
        {
            buttplug.SetSourceVibration(SourceId, 0f);
            return;
        }

        float interval = Mathf.Max(0.05f, KKButtplug.MilkPulseInterval.Value);

        // Burst duration is derived from interval.
        // This keeps compatibility with your config and prevents hardware smoothing.
        float burstDuration = Mathf.Clamp(interval * 0.25f, 0.08f, 0.25f);

        if (!_burstActive)
        {
            _intervalTimer -= Time.unscaledDeltaTime;

            if (_intervalTimer <= 0f)
            {
                _burstActive = true;
                _burstTimer = burstDuration;

                _pulsesRemaining--;
            }

            buttplug.SetSourceVibration(SourceId, 0f);
        }
        else
        {
            _burstTimer -= Time.unscaledDeltaTime;

            float strength = Mathf.Clamp01(KKButtplug.MilkVibration.Value);
            buttplug.SetSourceVibration(SourceId, strength);

            if (_burstTimer <= 0f)
            {
                _burstActive = false;
                _intervalTimer = interval - burstDuration;
            }
        }
    }
}