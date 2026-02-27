using BepInEx;
using UnityEngine;
using UnityEngine.InputSystem;

// uGUI
using UnityEngine.UI;

using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Generic;

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

    private void Awake()
    {
        Logger.LogInfo("========== KK BUTTPLUG (Multi-device WS Mode) ==========");
        Logger.LogInfo("F9  = Toggle UI");
        Logger.LogInfo("F10 = Connect/Scan");
        Logger.LogInfo("F11 = Vibrate 50% (all devices)");
        Logger.LogInfo("F12 = Stop (all devices)");

        // IMPORTANT: Don't create fonts/UI in Awake in KK (graphics device can be null early)
        StartCoroutine(InitUiWhenReady());
    }

    private System.Collections.IEnumerator InitUiWhenReady()
    {
        // Wait a couple frames for Unity to be properly initialized
        yield return null;
        yield return null;

        // If we are still too early (graphics device not ready), wait up to 10 seconds
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
            SendVibrateAll(0.5f);
        }

        if (Keyboard.current.f12Key.wasPressedThisFrame)
        {
            Logger.LogInfo("F12 pressed");
            SendStopAll();
        }

        // Main-thread UI refresh
        if (_uiDirty)
        {
            _uiDirty = false;
            RefreshUI();
        }
    }

    private void OnDestroy()
    {
        // Don't leave threads/sockets running
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

                // Stop scanning once when we have at least 1 device
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

                    Logger.LogInfo("Scanning stopped (device(s) ready).");
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
            // DeviceList: replace full list
            if (json.Contains("DeviceList"))
            {
                var matches = Regex.Matches(json, @"""DeviceIndex"":\s*(\d+)");
                lock (_deviceIndices)
                {
                    _deviceIndices.Clear();
                    foreach (Match m in matches)
                        _deviceIndices.Add(int.Parse(m.Groups[1].Value));
                }

                Logger.LogInfo("DeviceList received. Devices now: " + GetDeviceCount());
                MarkUiDirty();
                return;
            }

            // DeviceAdded: add one
            if (json.Contains("DeviceAdded"))
            {
                var match = Regex.Match(json, @"""DeviceIndex"":\s*(\d+)");
                if (match.Success)
                {
                    int idx = int.Parse(match.Groups[1].Value);
                    lock (_deviceIndices) _deviceIndices.Add(idx);
                    Logger.LogInfo("Device added. Index = " + idx + " | Total = " + GetDeviceCount());
                    MarkUiDirty();
                }
                return;
            }

            // DeviceRemoved: remove one
            if (json.Contains("DeviceRemoved"))
            {
                var match = Regex.Match(json, @"""DeviceIndex"":\s*(\d+)");
                if (match.Success)
                {
                    int idx = int.Parse(match.Groups[1].Value);
                    lock (_deviceIndices) _deviceIndices.Remove(idx);
                    Logger.LogInfo("Device removed. Index = " + idx + " | Total = " + GetDeviceCount());

                    // If no devices remain, allow scanning again (optional)
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
        {
            Logger.LogWarning("No device ready.");
            return;
        }

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

        Logger.LogInfo($"Vibrate sent to {indices.Length} device(s).");
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

        Logger.LogInfo($"Stop sent to {indices.Length} device(s).");
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

    private void MarkUiDirty()
    {
        _uiDirty = true;
    }

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
        // In KoboldKare, Resources.GetBuiltinResource("Arial.ttf") errors/crashes early.
        // Use OS font, but only AFTER graphics device is ready (InitUiWhenReady handles that).
        try
        {
            var font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Tahoma" }, size);
            if (font == null)
                Logger.LogError("[KKButtplug] OS font fallback returned null.");
            else
                Logger.LogInfo($"[KKButtplug] Using OS font: {font.name}");
            return font;
        }
        catch (Exception ex)
        {
            Logger.LogError("[KKButtplug] GetSafeFont failed: " + ex);
            return null;
        }
    }

    private void BuildUI()
    {
        // Root
        _uiRoot = new GameObject("KKButtplug_UI");
        DontDestroyOnLoad(_uiRoot);

        // Canvas
        _canvas = _uiRoot.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 9999;

        _uiRoot.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _uiRoot.AddComponent<GraphicRaycaster>();

        // Panel
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

        // Don't rely on the 4-arg ctor; some ref sets in your environment hide it.
        layout.padding = new RectOffset();
        layout.padding.left = 12;
        layout.padding.right = 12;
        layout.padding.top = 12;
        layout.padding.bottom = 12;

        layout.spacing = 8f;

        panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Font (safe)
        Font font = GetSafeFont(14);

        // Title
        CreateText(panel.transform, "KK Buttplug", font, 18, FontStyle.Bold);

        _statusText = CreateText(panel.transform, "Connected: false", font, 14, FontStyle.Normal);
        _countText = CreateText(panel.transform, "Devices: 0", font, 14, FontStyle.Normal);

        // Buttons row
        var row = new GameObject("ButtonRow");
        row.transform.SetParent(panel.transform, false);
        var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childForceExpandWidth = true;

        CreateButton(row.transform, "Connect / Scan", font, () => StartWebSocket());
        CreateButton(row.transform, "Stop All", font, () => SendStopAll());

        CreateButton(panel.transform, "Vibrate All (50%)", font, () =>
        {
            SendVibrateAll(0.5f);
        });

        // Device list
        CreateText(panel.transform, "Device Indices", font, 14, FontStyle.Bold);
        _deviceListText = CreateText(panel.transform, "(none)", font, 14, FontStyle.Normal);
        _deviceListText.alignment = TextAnchor.UpperLeft;

        // Give multiline list room
        var listLe = _deviceListText.gameObject.GetComponent<LayoutElement>();
        if (listLe != null) listLe.minHeight = 140f;

        _uiRoot.SetActive(_showUi);
    }

    private Text CreateText(Transform parent, string text, Font font, int size, FontStyle style)
    {
        // Guard: if font creation failed, don't crash. Keep layout stable.
        if (font == null)
        {
            var placeholder = new GameObject("Text_Placeholder");
            placeholder.transform.SetParent(parent, false);
            var lePh = placeholder.AddComponent<LayoutElement>();
            lePh.minHeight = 22f;

            var tPh = placeholder.AddComponent<Text>();
            tPh.text = "";
            tPh.raycastTarget = false;
            return tPh;
        }

        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 0f); // let layout drive size

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

        // Don't block clicks
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
        txt.font = font; // can be null if font failed; in that case, label won't render but won't crash
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