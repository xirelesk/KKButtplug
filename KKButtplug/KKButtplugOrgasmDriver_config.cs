using UnityEngine;
using Photon.Pun;

public class KKButtplugOrgasmDriver : MonoBehaviour
{
    [Header("Refs")]
    public Kobold kobold;
    public KKButtplug buttplug;

    private const string SourceId = "orgasm";

    private bool _maleActive = false;
    private float _femaleTimer = 0f;
    private float _patternTimer = 0f;

    private void OnEnable()
    {
        KKButtplugOrgasmHooks.MaleOrgasmStarted += OnMaleStart;
        KKButtplugOrgasmHooks.MaleOrgasmEnded += OnMaleEnd;
        KKButtplugOrgasmHooks.FemaleOrgasmTriggered += OnFemaleTrigger;
    }

    private void OnDisable()
    {
        KKButtplugOrgasmHooks.MaleOrgasmStarted -= OnMaleStart;
        KKButtplugOrgasmHooks.MaleOrgasmEnded -= OnMaleEnd;
        KKButtplugOrgasmHooks.FemaleOrgasmTriggered -= OnFemaleTrigger;

        if (buttplug != null)
            buttplug.ClearSource(SourceId);
    }

    private bool IsLocal()
    {
        var pv = kobold != null ? kobold.GetComponent<PhotonView>() : null;
        return pv != null && pv.IsMine;
    }

    private void OnMaleStart(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal()) return;

        _maleActive = true;
        _patternTimer = 0f;
    }

    private void OnMaleEnd(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal()) return;

        _maleActive = false;
    }

    private void OnFemaleTrigger(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal()) return;

        _femaleTimer = KKButtplug.FemaleOrgasmDuration.Value;
        _patternTimer = 0f;
    }

    private void Update()
    {
        if (kobold == null || buttplug == null || !IsLocal())
            return;

        bool active = false;

        if (_maleActive)
        {
            active = true;
        }
        else if (_femaleTimer > 0f)
        {
            _femaleTimer -= Time.unscaledDeltaTime;
            if (_femaleTimer < 0f) _femaleTimer = 0f;
            active = true;
        }

        if (!active)
        {
            buttplug.SetSourceVibration(SourceId, 0f);
            return;
        }

        float baseStrength = Mathf.Clamp01(KKButtplug.OrgasmVibration.Value);

        if (!KKButtplug.UseOrgasmPattern.Value)
        {
            buttplug.SetSourceVibration(SourceId, baseStrength);
            return;
        }

        // Read config safely
        float buzz = Mathf.Max(0.02f, KKButtplug.OrgasmBuzzDuration.Value);
        float pause = Mathf.Max(0.02f, KKButtplug.OrgasmPauseDuration.Value);
        float cycle = buzz + pause;

        _patternTimer += Time.unscaledDeltaTime;
        if (_patternTimer >= cycle)
            _patternTimer -= cycle;

        if (_patternTimer <= buzz)
        {
            buttplug.SetSourceVibration(SourceId, baseStrength);
        }
        else
        {
            buttplug.SetSourceVibration(SourceId, 0f);
        }
    }
}