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

    // Guard against stale events when /swap or manual reattach happens.
    private int _localGeneration = -1;

    // Pattern timer
    private float _patternTimer = 0f;

    private void OnEnable()
    {
        _localGeneration = KKButtplugOrgasmHooks.OrgasmGeneration;

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

    private bool IsStale()
    {
        return _localGeneration != KKButtplugOrgasmHooks.OrgasmGeneration;
    }

    private void OnMaleStart(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal() || IsStale()) return;

        _maleActive = true;
        _patternTimer = 0f;
    }

    private void OnMaleEnd(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal() || IsStale()) return;

        _maleActive = false;
    }

    private void OnFemaleTrigger(Kobold who)
    {
        if (kobold == null || who != kobold || !IsLocal() || IsStale()) return;

        _femaleTimer = Mathf.Max(_femaleTimer, KKButtplug.FemaleOrgasmDuration.Value);
        _patternTimer = 0f;
    }

    private void Update()
    {
        if (kobold == null || buttplug == null || !IsLocal())
            return;

        if (IsStale())
        {
            // We were detached while this driver was active.
            buttplug.SetSourceVibration(SourceId, 0f);
            _maleActive = false;
            _femaleTimer = 0f;
            _patternTimer = 0f;
            return;
        }

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

        // ===== BURST PATTERN (phone buzz) =====
        float buzz = Mathf.Max(0.01f, KKButtplug.OrgasmBuzzDuration.Value);
        float pause = Mathf.Max(0.00f, KKButtplug.OrgasmPauseDuration.Value);
        float cycle = buzz + pause;

        _patternTimer += Time.unscaledDeltaTime;

        if (cycle <= 0.001f)
        {
            buttplug.SetSourceVibration(SourceId, baseStrength);
            return;
        }

        if (_patternTimer >= cycle)
            _patternTimer -= cycle;

        if (_patternTimer <= buzz)
            buttplug.SetSourceVibration(SourceId, baseStrength);
        else
            buttplug.SetSourceVibration(SourceId, 0f);
    }
}