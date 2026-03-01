using BepInEx;
using UnityEngine;
using UnityEngine.InputSystem;
using Photon.Pun;

// uGUI
using UnityEngine.UI;

using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Generic;

using HarmonyLib;

[BepInPlugin("xirelesk.kkbuttplug", "KK Buttplug", "1.0.0")]
public class KKButtplug : BaseUnityPlugin
{
    private ClientWebSocket _ws;
    private CancellationTokenSource _cts;
    private Thread _wsThread;

    private bool _connected = false;
    private int _msgId = 1;

    // Multi-device tracking
    private readonly HashSet<int> _deviceIndices = new HashSet<int>();

    // Prevent stop-scan spam
    private bool _scanStopSent = false;

    // ===== Config =====
    public static BepInEx.Configuration.ConfigEntry<string> ServerUrlConfig;
    public static BepInEx.Configuration.ConfigEntry<float> MinVibration;
    public static BepInEx.Configuration.ConfigEntry<float> MaxVibration;
    public static BepInEx.Configuration.ConfigEntry<float> OrgasmVibration;
    public static BepInEx.Configuration.ConfigEntry<float> FemaleOrgasmDuration;
    public static BepInEx.Configuration.ConfigEntry<bool> UseOrgasmPattern;
    public static BepInEx.Configuration.ConfigEntry<float> OrgasmBuzzDuration;
    public static BepInEx.Configuration.ConfigEntry<float> OrgasmPauseDuration;
    public static BepInEx.Configuration.ConfigEntry<float> MilkVibration;
    public static BepInEx.Configuration.ConfigEntry<float> MilkPulseInterval;
    public static BepInEx.Configuration.ConfigEntry<int> MilkPulseCount;

    // ===== Source-mixed vibration (no smoothing) =====
    // Each driver writes a named strength (0..1). We send max(strengths).
    private readonly Dictionary<string, float> _sourceStrength = new Dictionary<string, float>();
    private float _lastMixedSent = -1f;

    public void SetVibration(float strength) => SetSourceVibration("manual", strength);

    public void SetSourceVibration(string source, float strength)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        strength = Mathf.Clamp01(strength);

        lock (_sourceStrength)
            _sourceStrength[source] = strength;
    }

    public void ClearSource(string source)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        lock (_sourceStrength)
            _sourceStrength.Remove(source);
    }

    public void StopAll()
    {
        if (!_connected || _ws == null)
            return;

        lock (_sourceStrength)
            _sourceStrength.Clear();

        _lastMixedSent = -1f;
        SendStopAll();
    }

    private void ApplyMixedVibration()
    {
        if (!_connected || _ws == null)
            return;

        float mixed = 0f;
        lock (_sourceStrength)
        {
            foreach (var kv in _sourceStrength)
                if (kv.Value > mixed) mixed = kv.Value;
        }

        mixed = Mathf.Clamp01(mixed);

        // Avoid redundant WS spam if identical
        if (Mathf.Abs(mixed - _lastMixedSent) < 0.0001f)
            return;

        _lastMixedSent = mixed;

        // IMPORTANT:
        // Use VibrateCmd(0) instead of StopDeviceCmd for "off" phases,
        // otherwise fast buzz patterns get eaten by stop/start behavior.
        SendVibrateAll(mixed);
    }

    // ===== Overlay UI (uGUI) =====
    private GameObject _uiRoot;
    private Canvas _canvas;
    private Text _statusText;
    private Text _countText;
    private Text _deviceListText;

    private bool _showUi = true;
    private volatile bool _uiDirty = true;

    // ===== Driver bootstrap (local player only) =====
    private float _attachScanTimer = 0f;
    private bool _driversAttached = false;

    // Respawn-safe attachment
    private Kobold _attachedLocalKobold = null;
    private int _attachedLocalViewId = 0;

    private Harmony _harmony;

    private void Awake()
    {
        // ===== Config Bindings =====
        ServerUrlConfig = Config.Bind(
            "Connection",
            "ServerUrl",
            "ws://127.0.0.1:12345",
            "WebSocket URL for Intiface server."
        );

        MinVibration = Config.Bind(
            "Vibration",
            "MinVibration",
            0.10f,
            "Minimum vibration strength while active."
        );

        MaxVibration = Config.Bind(
            "Vibration",
            "MaxVibration",
            0.7f,
            "Maximum vibration strength during stimulation."
        );

        OrgasmVibration = Config.Bind(
            "Vibration",
            "OrgasmVibration",
            1.0f,
            "Vibration strength during orgasm."
        );

        UseOrgasmPattern = Config.Bind(
    "Orgasm",
    "UseOrgasmPattern",
    true,
    "If true, orgasm uses a pulsing vibration pattern instead of constant strength."
);

        OrgasmBuzzDuration = Config.Bind(
    "Orgasm",
    "OrgasmBuzzDuration",
    0.08f,
    "Duration (seconds) of each buzz burst during orgasm pattern. WARNING: Values below 0.05 may not register on some devices."
);

        OrgasmPauseDuration = Config.Bind(
            "Orgasm",
            "OrgasmPauseDuration",
            0.12f,
            "Duration (seconds) of pause between buzz bursts. WARNING: Very low values may be smoothed by hardware and feel continuous."
        );

        FemaleOrgasmDuration = Config.Bind(
            "Orgasm",
            "FemaleOrgasmDuration",
            5f,
            "Duration in seconds of female orgasm vibration."
        );

        MilkVibration = Config.Bind(
            "Milking",
            "MilkVibration",
            0.4f,
            "Vibration strength during each milking pulse."
        );

        MilkPulseInterval = Config.Bind(
            "Milking",
            "MilkPulseInterval",
            1.0f,
            "Seconds per milking pulse. (Game default is 1.0s)"
        );

        MilkPulseCount = Config.Bind(
            "Milking",
            "MilkPulseCount",
            12,
            "Number of pulses per milking event. (Game default is 12)"
        );


        Logger.LogInfo("========== KK BUTTPLUG ==========");
        Logger.LogInfo("F9 = Toggle UI (Connect is UI-only)");

        try
        {
            _harmony = new Harmony("xirelesk.kkbuttplug.harmony");
            _harmony.PatchAll();
            Logger.LogInfo("[KKButtplug] Harmony patches applied.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[KKButtplug] Harmony patch failed: " + ex);
        }

        StartCoroutine(InitUiWhenReady());
    }

    private System.Collections.IEnumerator InitUiWhenReady()
    {
        yield return null;
        yield return null;

        float timeout = 10f;
        while (timeout > 0f && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }

        try
        {
            BuildUI();
            MarkUiDirty();
            Logger.LogInfo("[KKButtplug] UI initialized.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[KKButtplug] UI init failed: " + ex);
        }
    }

    private void Update()
    {
        if (Keyboard.current == null)
            return;

        // Only hotkey left: UI toggle
        if (Keyboard.current.f9Key.wasPressedThisFrame)
        {
            _showUi = !_showUi;
            if (_uiRoot != null) _uiRoot.SetActive(_showUi);
        }

        if (_uiDirty)
        {
            _uiDirty = false;
            RefreshUI();
        }

        BootstrapDrivers();
        ApplyMixedVibration();
    }

    private Kobold FindLocalKobold()
    {
        var kobolds = FindObjectsOfType<Kobold>();
        if (kobolds == null || kobolds.Length == 0)
            return null;

        foreach (var k in kobolds)
        {
            if (k == null) continue;
            var pv = k.GetComponent<PhotonView>();
            if (pv != null && pv.IsMine)
                return k;
        }

        return null;
    }

    private void ResetDriverAttachmentState(string reason)
    {
        StopAll();

        _driversAttached = false;
        _attachedLocalKobold = null;
        _attachedLocalViewId = 0;

        Logger.LogInfo("[KKButtplug] Driver attachment reset: " + reason);
    }

    private void BootstrapDrivers()
    {
        if (_driversAttached)
        {
            if (_attachedLocalKobold == null || _attachedLocalKobold.gameObject == null)
            {
                ResetDriverAttachmentState("attached kobold destroyed (respawn?)");
            }
            else
            {
                var pv = _attachedLocalKobold.GetComponent<PhotonView>();
                if (pv == null || !pv.IsMine)
                {
                    ResetDriverAttachmentState("attached kobold no longer local");
                }
                else if (_attachedLocalViewId != 0 && pv.ViewID != _attachedLocalViewId)
                {
                    ResetDriverAttachmentState("PhotonView changed (respawn?)");
                }
            }
        }

        if (_driversAttached)
            return;

        _attachScanTimer -= Time.unscaledDeltaTime;
        if (_attachScanTimer > 0f)
            return;
        _attachScanTimer = 0.5f;

        try
        {
            Kobold local = FindLocalKobold();
            if (local == null)
                return;

            var localPv = local.GetComponent<PhotonView>();
            int localViewId = localPv != null ? localPv.ViewID : 0;

            if (_attachedLocalKobold != null && local != _attachedLocalKobold)
            {
                ResetDriverAttachmentState("local kobold instance changed (respawn)");
            }

            var recv = local.GetComponent<KKButtplugReceivingDriver>();
            if (recv == null) recv = local.gameObject.AddComponent<KKButtplugReceivingDriver>();
            recv.kobold = local;
            recv.buttplug = this;

            var give = local.GetComponent<KKButtplugGivingDriver>();
            if (give == null) give = local.gameObject.AddComponent<KKButtplugGivingDriver>();
            give.kobold = local;
            give.buttplug = this;

            var org = local.GetComponent<KKButtplugOrgasmDriver>();
            if (org == null) org = local.gameObject.AddComponent<KKButtplugOrgasmDriver>();
            org.kobold = local;
            org.buttplug = this;

            var milk = local.GetComponent<KKButtplugMilkingDriver>();
            if (milk == null) milk = local.gameObject.AddComponent<KKButtplugMilkingDriver>();
            milk.kobold = local;
            milk.buttplug = this;

            _attachedLocalKobold = local;
            _attachedLocalViewId = localViewId;
            _driversAttached = true;

            Logger.LogInfo($"[KKButtplug] Attached drivers to local player: {local.name} (ViewID={_attachedLocalViewId})");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[KKButtplug] Failed to attach drivers (will retry): " + ex.Message);
        }
    }

    private void OnDestroy()
    {
        try { _harmony?.UnpatchSelf(); } catch { }

        try { _cts?.Cancel(); } catch { }
        CleanupSocket();

        if (_uiRoot != null)
        {
            try { Destroy(_uiRoot); } catch { }
            _uiRoot = null;
        }
    }

    // UI button calls this
    private void OnConnectButton()
    {
        Logger.LogInfo("[KKButtplug] Connect button clicked.");
        StartWebSocket();
        MarkUiDirty();
    }

    private void StartWebSocket()
    {
        if (_connected)
        {
            Logger.LogWarning("Already connected.");
            return;
        }

        _wsThread = new Thread(WebSocketThread) { IsBackground = true };
        _wsThread.Start();
    }

    private void WebSocketThread()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            Logger.LogInfo("Connecting to Intiface...");
            _ws.ConnectAsync(new Uri(ServerUrlConfig.Value), _cts.Token).GetAwaiter().GetResult();

            _connected = true;
            lock (_deviceIndices) _deviceIndices.Clear();
            _scanStopSent = false;

            Logger.LogInfo("Connected!");
            MarkUiDirty();

            SendJson($@"[
                {{
                    ""RequestServerInfo"": {{
                        ""Id"": {_msgId++},
                        ""ClientName"": ""KK Mod"",
                        ""MessageVersion"": 3
                    }}
                }}
            ]");

            SendJson($@"[
                {{
                    ""RequestDeviceList"": {{
                        ""Id"": {_msgId++}
                    }}
                }}
            ]");

            SendJson($@"[
                {{
                    ""StartScanning"": {{
                        ""Id"": {_msgId++}
                    }}
                }}
            ]");

            Logger.LogInfo("Scanning started.");

            var buffer = new byte[4096];

            while (_ws.State == WebSocketState.Open)
            {
                string msg = ReceiveFullTextMessage(buffer);
                if (msg == null)
                    break;

                if (msg.Contains("Device") || msg.Contains("Error"))
                    Logger.LogInfo("WS: " + msg);

                HandleIncoming(msg);

                if (!_scanStopSent && GetDeviceCount() > 0)
                {
                    _scanStopSent = true;

                    SendJson($@"[
                        {{
                            ""StopScanning"": {{
                                ""Id"": {_msgId++}
                            }}
                        }}
                    ]");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("WebSocket error: " + ex);
        }

        CleanupSocket();
        MarkUiDirty();

        Logger.LogWarning("Disconnected. Reconnecting in 3 seconds...");
        Thread.Sleep(3000);
        StartWebSocket();
    }

    private int GetDeviceCount()
    {
        lock (_deviceIndices) return _deviceIndices.Count;
    }

    private string ReceiveFullTextMessage(byte[] buffer)
    {
        try
        {
            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var result = _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token)
                                   .GetAwaiter().GetResult();

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.LogWarning("Server closed connection.");
                        return null;
                    }

                    ms.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                        break;
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Receive error: " + ex);
            return null;
        }
    }

    private void HandleIncoming(string json)
    {
        try
        {
            if (json.Contains("DeviceList"))
            {
                var matches = Regex.Matches(json, @"""DeviceIndex"":\s*(\d+)");
                lock (_deviceIndices)
                {
                    _deviceIndices.Clear();
                    foreach (Match m in matches)
                        _deviceIndices.Add(int.Parse(m.Groups[1].Value));
                }
                MarkUiDirty();
                return;
            }

            if (json.Contains("DeviceAdded"))
            {
                var match = Regex.Match(json, @"""DeviceIndex"":\s*(\d+)");
                if (match.Success)
                {
                    int idx = int.Parse(match.Groups[1].Value);
                    lock (_deviceIndices) _deviceIndices.Add(idx);
                    MarkUiDirty();
                }
                return;
            }

            if (json.Contains("DeviceRemoved"))
            {
                var match = Regex.Match(json, @"""DeviceIndex"":\s*(\d+)");
                if (match.Success)
                {
                    int idx = int.Parse(match.Groups[1].Value);
                    lock (_deviceIndices) _deviceIndices.Remove(idx);

                    if (GetDeviceCount() == 0)
                        _scanStopSent = false;

                    MarkUiDirty();
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Parse error: " + ex);
        }
    }

    private void SendVibrateAll(float strength)
    {
        if (!_connected) return;

        strength = Mathf.Clamp01(strength);

        int[] indices;
        lock (_deviceIndices)
            indices = new List<int>(_deviceIndices).ToArray();

        if (indices.Length == 0) return;

        foreach (var idx in indices)
        {
            SendJson($@"[
                {{
                    ""VibrateCmd"": {{
                        ""Id"": {_msgId++},
                        ""DeviceIndex"": {idx},
                        ""Speeds"": [
                            {{ ""Index"": 0, ""Speed"": {strength.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}
                        ]
                    }}
                }}
            ]");
        }
    }

    private void SendStopAll()
    {
        if (!_connected) return;

        int[] indices;
        lock (_deviceIndices)
            indices = new List<int>(_deviceIndices).ToArray();

        if (indices.Length == 0) return;

        foreach (var idx in indices)
        {
            SendJson($@"[
                {{
                    ""StopDeviceCmd"": {{
                        ""Id"": {_msgId++},
                        ""DeviceIndex"": {idx}
                    }}
                }}
            ]");
        }
    }

    private void SendJson(string json)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _cts.Token
            ).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError("Send error: " + ex);
        }
    }

    private void CleanupSocket()
    {
        try { _connected = false; } catch { }
        try { _scanStopSent = false; } catch { }
        lock (_deviceIndices) _deviceIndices.Clear();

        try
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                   .GetAwaiter().GetResult();
            }
        }
        catch { }

        try { _ws?.Dispose(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }

        _ws = null;
        _cts = null;
    }

    // ===== UI Helpers =====

    private void MarkUiDirty() => _uiDirty = true;

    private void RefreshUI()
    {
        if (_uiRoot == null) return;

        if (_statusText != null)
            _statusText.text = $"Connected: {_connected}";

        if (_countText != null)
            _countText.text = $"Devices: {GetDeviceCount()}";

        if (_deviceListText != null)
        {
            int[] indices;
            lock (_deviceIndices)
                indices = new List<int>(_deviceIndices).ToArray();

            if (indices.Length == 0)
            {
                _deviceListText.text = "(none)";
            }
            else
            {
                Array.Sort(indices);
                var sb = new StringBuilder();
                for (int i = 0; i < indices.Length; i++)
                    sb.AppendLine($"• Device {indices[i]}");
                _deviceListText.text = sb.ToString();
            }
        }
    }

    private Font GetSafeFont(int size)
    {
        try { return Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Tahoma" }, size); }
        catch { return null; }
    }

    private void BuildUI()
    {
        _uiRoot = new GameObject("KKButtplug_UI");
        DontDestroyOnLoad(_uiRoot);

        _canvas = _uiRoot.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 9999;

        _uiRoot.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _uiRoot.AddComponent<GraphicRaycaster>();

        var panel = new GameObject("Panel");
        panel.transform.SetParent(_uiRoot.transform, false);

        var panelRect = panel.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0f, 1f);
        panelRect.anchorMax = new Vector2(0f, 1f);
        panelRect.pivot = new Vector2(0f, 1f);
        panelRect.anchoredPosition = new Vector2(20f, -20f);
        panelRect.sizeDelta = new Vector2(360f, 420f);

        var panelImg = panel.AddComponent<Image>();
        panelImg.color = new Color(0f, 0f, 0f, 0.65f);

        var layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.padding = new RectOffset { left = 12, right = 12, top = 12, bottom = 12 };
        layout.spacing = 8f;

        panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Font font = GetSafeFont(14);

        CreateText(panel.transform, "KK Buttplug", font, 18, FontStyle.Bold);
        _statusText = CreateText(panel.transform, "Connected: false", font, 14, FontStyle.Normal);
        _countText = CreateText(panel.transform, "Devices: 0", font, 14, FontStyle.Normal);

        // ===== Button Row =====
        var row = new GameObject("ButtonRow");
        row.transform.SetParent(panel.transform, false);

        var rowLe = row.AddComponent<LayoutElement>();
        rowLe.minHeight = 32f;
        rowLe.preferredHeight = 32f;

        var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.childForceExpandHeight = true;
        rowLayout.childForceExpandWidth = true;

        // ONLY Connect button now
        CreateButton(row.transform, "Connect / Scan", font, () => StartWebSocket());

        CreateText(panel.transform, "Device Indices", font, 14, FontStyle.Bold);
        _deviceListText = CreateText(panel.transform, "(none)", font, 14, FontStyle.Normal);
        _deviceListText.alignment = TextAnchor.UpperLeft;

        var listLe = _deviceListText.gameObject.GetComponent<LayoutElement>();
        if (listLe != null) listLe.minHeight = 140f;

        _uiRoot.SetActive(_showUi);
    }

    private Text CreateText(Transform parent, string text, Font font, int size, FontStyle style)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);

        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 22f;

        var t = go.AddComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = Color.white;
        t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.alignment = TextAnchor.MiddleLeft;
        t.raycastTarget = false;

        return t;
    }

    private Button CreateButton(Transform parent, string label, Font font, Action onClick)
    {
        var go = new GameObject($"Button_{label}");
        go.transform.SetParent(parent, false);

        // ✅ FIX: LayoutElement gives layout groups a real height/width to use
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = 32f;
        le.preferredHeight = 32f;
        le.flexibleWidth = 1f;

        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.15f);

        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(() => onClick?.Invoke());

        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 32f);

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);

        var txt = textGo.AddComponent<Text>();
        txt.font = font;
        txt.fontSize = 14;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.text = label;
        txt.raycastTarget = false;

        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        return btn;
    }
}