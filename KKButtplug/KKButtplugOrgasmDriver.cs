using UnityEngine;
using Photon.Pun;

public class KKButtplugOrgasmDriver : MonoBehaviour
{
    [Header("Refs")]
    public Kobold kobold;
    public KKButtplug buttplug;

    [Header("Output")]
    [Range(0f, 1f)] public float orgasmVibration = 1.0f;

    [Header("Female Orgasm Duration (no-dick)")]
    public float femaleOrgasmDurationSeconds = 3.0f;

    private const string SourceId = "orgasm";

    private bool _maleActive = false;
    private float _femaleTimer = 0f;

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
        if (kobold == null || who == null) return;
        if (who != kobold) return;
        if (!IsLocal()) return;

        _maleActive = true;
    }

    private void OnMaleEnd(Kobold who)
    {
        if (kobold == null || who == null) return;
        if (who != kobold) return;
        if (!IsLocal()) return;

        _maleActive = false;
    }

    private void OnFemaleTrigger(Kobold who)
    {
        if (kobold == null || who == null) return;
        if (who != kobold) return;
        if (!IsLocal()) return;

        _femaleTimer = Mathf.Max(_femaleTimer, femaleOrgasmDurationSeconds);
    }

    private void Update()
    {
        if (kobold == null || buttplug == null) return;
        if (!IsLocal()) return;

        bool active = false;

        if (_maleActive)
        {
            active = true;
        }
        else if (_femaleTimer > 0f)
        {
            _femaleTimer -= Time.unscaledDeltaTime;
            active = true;
        }

        if (active)
            buttplug.SetSourceVibration(SourceId, orgasmVibration);
        else
            buttplug.SetSourceVibration(SourceId, 0f);
    }
}
