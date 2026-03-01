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

[BepInPlugin("com.yourname.kkbuttplug", "KK Buttplug", "7.1.0")]
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

    // ===== Source-mixed vibration (no smoothing) =====
    // Each driver writes a named strength (0..1). We send max(strengths).
    private readonly Dictionary<string, float> _sourceStrength = new Dictionary<string, float>();
    private float _lastMixedSent = -1f;

    /// <summary>
    /// Back-compat: set vibration as "manual" source.
    /// </summary>
    public void SetVibration(float strength) => SetSourceVibration("manual", strength);

    /// <summary>
    /// Set vibration contribution from a named source (0..1).
    /// Final output is max of all sources. No smoothing.
    /// </summary>
    public void SetSourceVibration(string source, float strength)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        strength = Mathf.Clamp01(strength);

        lock (_sourceStrength)
            _sourceStrength[source] = strength;
    }

    /// <summary>
    /// Clear a named source contribution.
    /// </summary>
    public void ClearSource(string source)
    {
        if (string.IsNullOrEmpty(source)) source = "default";
        lock (_sourceStrength)
            _sourceStrength.Remove(source);
    }

    private void ClearAllSources()
    {
        lock (_sourceStrength)
            _sourceStrength.Clear();
        _lastMixedSent = -1f;
    }

    /// <summary>
    /// Stop all connected devices and clear all sources.
    /// </summary>
    public void StopAll()
    {
        if (!_connected || _ws == null)
            return;

        ClearAllSources();
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

        // No smoothing, but avoid redundant WS spam if identical
        if (Mathf.Abs(mixed - _lastMixedSent) < 0.0001f)
            return;

        _lastMixedSent = mixed;

        if (mixed <= 0.001f)
            SendStopAll();
        else
            SendVibrateAll(mixed);
    }

    private const string ServerUrl = "ws://127.0.0.1:12345";

    // ===== Overlay UI (uGUI) =====
    private GameObject _uiRoot;
    private Canvas _canvas;
    private Text _statusText;
    private Text _countText;
    private Text _deviceListText;

    private bool _showUi = true;

    // Thread -> main thread UI refresh
    private volatile bool _uiDirty = true;

    // ===== Driver bootstrap (local player only) =====
    private float _attachScanTimer = 0f;

    // Track which local kobold we're attached to (so reset/rejoin reattaches)
    private Kobold _attachedKobold = null;
    private int _attachedKoboldViewId = -1;

    // Harmony (for orgasm hooks)
    private Harmony _harmony;

    private void Awake()
    {
        Logger.LogInfo("========== KK BUTTPLUG (Multi-device WS Mode) ==========");
        Logger.LogInfo("F9  = Toggle UI");
        Logger.LogInfo("F10 = Connect/Scan");
        Logger.LogInfo("F11 = Vibrate 50% (manual)");
        Logger.LogInfo("F12 = Stop (all devices)");

        try
        {
            _harmony = new Harmony("com.yourname.kkbuttplug.harmony");
            _harmony.PatchAll();
            Logger.LogInfo("[KKButtplug] Harmony patches applied.");
        }
        catch (Exception ex)
        {
            Logger.LogError("[KKButtplug] Harmony patch failed (orgasm hooks will not work): " + ex);
        }

        // IMPORTANT: Don't create fonts/UI in Awake in KK (graphics device can be null early)
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

        if (Keyboard.current.f9Key.wasPressedThisFrame)
        {
            _showUi = !_showUi;
            if (_uiRoot != null) _uiRoot.SetActive(_showUi);
        }

        if (Keyboard.current.f10Key.wasPressedThisFrame)
        {
            Logger.LogInfo("F10 pressed");
            StartWebSocket();
        }

        if (Keyboard.current.f11Key.wasPressedThisFrame)
        {
            Logger.LogInfo("F11 pressed");
            SetVibration(0.5f);
        }

        if (Keyboard.current.f12Key.wasPressedThisFrame)
        {
            Logger.LogInfo("F12 pressed");
            StopAll();
        }

        if (_uiDirty)
        {
            _uiDirty = false;
            RefreshUI();
        }

        BootstrapDriversResilient();
        ApplyMixedVibration();
    }

    // Resilient bootstrap: reattach after reset/rejoin (local kobold object changes)
    private void BootstrapDriversResilient()
    {
        _attachScanTimer -= Time.unscaledDeltaTime;
        if (_attachScanTimer > 0f)
            return;
        _attachScanTimer = 0.5f;

        try
        {
            var kobolds = FindObjectsOfType<Kobold>();
            if (kobolds == null || kobolds.Length == 0)
            {
                // If we previously had one and now none exist (scene transition), clear sources.
                if (_attachedKobold != null)
                {
                    Logger.LogInfo("[KKButtplug] No kobolds found (transition). Clearing sources + stopping devices.");
                    _attachedKobold = null;
                    _attachedKoboldViewId = -1;
                    ClearAllSources();
                    if (_connected && _ws != null) SendStopAll();
                }
                return;
            }

            Kobold local = null;
            PhotonView localPv = null;

            foreach (var k in kobolds)
            {
                if (k == null) continue;
                var pv = k.GetComponent<PhotonView>();
                if (pv != null && pv.IsMine)
                {
                    local = k;
                    localPv = pv;
                    break;
                }
            }

            if (local == null || localPv == null)
                return;

            // Detect respawn/rejoin: local player kobold object changed
            bool changed =
                _attachedKobold == null ||
                _attachedKoboldViewId != localPv.ViewID ||
                _attachedKobold != local;

            if (changed)
            {
                Logger.LogInfo($"[KKButtplug] Local kobold changed (old={_attachedKoboldViewId}, new={localPv.ViewID}). Rebinding drivers.");

                // Clear sources so old drivers don't hold a value
                ClearAllSources();

                // Optional: immediate stop to prevent edge-case stuck vibration during load
                if (_connected && _ws != null)
                    SendStopAll();

                _attachedKobold = local;
                _attachedKoboldViewId = localPv.ViewID;
            }

            // Ensure drivers exist on current local kobold (even after respawn)
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
            _ws.ConnectAsync(new Uri(ServerUrl), _cts.Token).GetAwaiter().GetResult();

            _connected = true;
            lock (_deviceIndices) _deviceIndices.Clear();
            _scanStopSent = false;

            Logger.LogInfo("Connected!");
            MarkUiDirty();

            // Handshake
            SendJson($@"[
                {{
                    ""RequestServerInfo"": {{
                        ""Id"": {_msgId++},
                        ""ClientName"": ""KK Mod"",
                        ""MessageVersion"": 3
                    }}
                }}
            ]");

            // Get already-connected devices
            SendJson($@"[
                {{
                    ""RequestDeviceList"": {{
                        ""Id"": {_msgId++}
                    }}
                }}
            ]");

            // Scan for new devices
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
        if (!_connected)
        {
            Logger.LogWarning("Not connected.");
            return;
        }

        strength = Mathf.Clamp01(strength);

        int[] indices;
        lock (_deviceIndices)
            indices = new List<int>(_deviceIndices).ToArray();

        if (indices.Length == 0)
            return;

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
        if (!_connected)
            return;

        int[] indices;
        lock (_deviceIndices)
            indices = new List<int>(_deviceIndices).ToArray();

        if (indices.Length == 0)
            return;

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

        var row = new GameObject("ButtonRow");
        row.transform.SetParent(panel.transform, false);
        var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = true;

        CreateButton(row.transform, "Connect / Scan", font, () => StartWebSocket());
        CreateButton(row.transform, "Stop All", font, () => StopAll());
        CreateButton(panel.transform, "Manual Vibrate (50%)", font, () => SetVibration(0.5f));

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