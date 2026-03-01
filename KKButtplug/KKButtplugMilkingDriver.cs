using UnityEngine;
using Photon.Pun;

public class KKButtplugMilkingDriver : MonoBehaviour
{
    public Kobold kobold;
    public KKButtplug buttplug;

    private const string SourceId = "milking";

    private int _pulsesRemaining = 0;
    private float _intervalTimer = 0f;

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
        _intervalTimer = KKButtplug.MilkPulseInterval.Value;
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

        float strength = Mathf.Clamp01(KKButtplug.MilkVibration.Value);
        buttplug.SetSourceVibration(SourceId, strength);

        _intervalTimer -= Time.unscaledDeltaTime;

        if (_intervalTimer <= 0f)
        {
            _pulsesRemaining--;

            if (_pulsesRemaining > 0)
                _intervalTimer = KKButtplug.MilkPulseInterval.Value;
        }
    }
}