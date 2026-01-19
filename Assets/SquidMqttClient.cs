using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static UnityEngine.Rendering.DebugUI;

public class SquidMqttClient : MonoBehaviour
{
    [Header("MQTT Broker")]
    [SerializeField] private string brokerIp = "10.0.0.10";
    [SerializeField] private int brokerPort = 1883;

    [Header("Device Info")]
    [SerializeField] private string deviceId = "squid-ctrl";
    [SerializeField] private string version = "1.0.0";

    private IMqttClient _client;
    private MqttClientOptions _options;

    private string _elementTopic = "quant/squid";
    private string _rootTopic = "quant";

    // Latest received language (default English)
    public string CurrentLanguage { get; private set; } = "en";
    public bool MQTTActive = true;

    // Reconnect control
    private CancellationTokenSource _cts;
    private Task _reconnectTask;
    private volatile bool _isReconnecting;

    private async void Start()
    {
        _cts = new CancellationTokenSource();
        await ConnectAndSubscribeAsync();
    }

    private async Task ConnectAndSubscribeAsync()
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        // Message handler (unchanged)
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        // Build and store options so we can reuse them for reconnects
        _options = new MqttClientOptionsBuilder()
            .WithClientId(deviceId)
            .WithTcpServer(brokerIp, brokerPort)
            .WithCleanSession()
            .Build();

        // On connect: (re)subscribe + publish connection message
        _client.ConnectedAsync += async e =>
        {
            Debug.Log("[MQTT] Connected to broker " + brokerIp + ":" + brokerPort);

            try
            {
                // Subscribe to quant/squid/# for set messages
                await _client.SubscribeAsync(
                    new MqttTopicFilterBuilder()
                        .WithTopic(_elementTopic + "/#")
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build());

                //// Subscribe to quant/# for general messages
                //await _client.SubscribeAsync(
                //    new MqttTopicFilterBuilder()
                //        .WithTopic(_rootTopic + "/#")
                //        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                //        .Build());

                Debug.Log("[MQTT] Subscribed to quant/squid/# and quant/#");

                await PublishConnectionAsync("connected");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MQTT] Post-connect setup failed: " + ex.Message);
            }
        };

        // On disconnect: start reconnect loop
        _client.DisconnectedAsync += async e =>
        {
            Debug.LogWarning($"[MQTT] Disconnected. Reason: {e.Reason} Exception: {e.Exception?.Message}");

            if (_cts == null || _cts.IsCancellationRequested)
                return;

            if (_isReconnecting)
                return;

            _isReconnecting = true;

            _reconnectTask = ReconnectLoopAsync(_cts.Token);
            try
            {
                await _reconnectTask;
            }
            finally
            {
                _isReconnecting = false;
            }
        };

        // Initial connect attempt (your original try/catch behavior preserved)
        try
        {
            await _client.ConnectAsync(_options, _cts.Token);
            // ConnectedAsync handler will do subscribe + publish
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT] Connection failed: " + ex.Message);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested && (_client == null || !_client.IsConnected))
        {
            attempt++;

            // exponential backoff up to 30 seconds
            int cappedAttempt = Math.Min(attempt, 6); // 2^6 = 64
            int delayMs = Math.Min(30_000, 500 * (int)Math.Pow(2, cappedAttempt));

            try
            {
                Debug.Log($"[MQTT] Reconnect attempt #{attempt}...");
                await _client.ConnectAsync(_options, ct);

                Debug.Log("[MQTT] Reconnected!");
                return; // ConnectedAsync will re-subscribe + publish
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MQTT] Reconnect failed: " + ex.Message);
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        if (!MQTTActive)
            return Task.CompletedTask; // IMPORTANT: do not return null

        try
        {
            var topic = e.ApplicationMessage.Topic;
            var payloadBytes = e.ApplicationMessage.Payload;
            var payload = payloadBytes != null
                ? Encoding.UTF8.GetString(payloadBytes)
                : string.Empty;

            Debug.Log($"[MQTT] RX {topic}: {payload}");

            var msg = JsonConvert.DeserializeObject<QuantumMessage>(payload);
            if (msg == null)
                return Task.CompletedTask;

            if (msg.sndr == deviceId)
                return Task.CompletedTask; // Ignore own messages

            if (msg.set != null)
            {
                HandleSet(msg, topic);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT] Error processing incoming message: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private async void HandleSet(QuantumMessage msg, string topic)
    {
        var controller = SquidGameController.Instance;
        if (controller == null)
            return;

        // Stage set
        if (!string.IsNullOrEmpty(msg.set.stage))
        {
            await controller.SetStageFromServer(msg.set.stage);
        }

        // Timer / difficulty / numplayers
        if (msg.set.timer.HasValue ||
            msg.set.difficulty.HasValue ||
            msg.set.numplayers.HasValue)
        {
            await controller.ApplySettingsFromServer(
                msg.set.timer,
                msg.set.difficulty,
                msg.set.numplayers
            );
        }

        // Language update
        if (!string.IsNullOrEmpty(msg.set.lang))
        {
            CurrentLanguage = msg.set.lang.ToLower();
            Debug.Log("[MQTT] Language set to " + CurrentLanguage);

            controller.SetLanguageFromServer(CurrentLanguage);
        }

        // NEW: Flip Players remotely
        if (msg.set.flipplayers.HasValue)
        {
            bool value = msg.set.flipplayers.Value;
            Debug.Log("[MQTT] FlipPlayers command received: " + value);
            controller.ApplyFlipPlayers(value);
        }
    }

    // ------------------------------------------------------------------------
    // Public Publish API
    // ------------------------------------------------------------------------

    public async Task PublishConnectionAsync(string trig = "connected")
    {
        var message = new QuantumMessage
        {
            sndr = deviceId,
            con = "eth",
            ip = GetLocalIPAddress(),
            vers = version,
            trig = trig
        };

        await PublishAsync($"{_elementTopic}/{deviceId}", message);
    }

    public async Task PublishStageAsync(string stage, string trig = "logic",
                                        int? hits = null, int? misses = null, float? timeLeft = null)
    {
        var inf = new InfPayload
        {
            stage = stage,
            hits = hits,
            misses = misses,
            timeLeft = timeLeft
        };

        var message = new QuantumMessage
        {
            sndr = deviceId,
            inf = inf,
            trig = trig
        };

        await PublishAsync(_elementTopic, message);
    }

    public async Task PublishUserInputAsync(string stage, string inputName)
    {
        var inf = new InfPayload
        {
            stage = stage
        };

        var message = new QuantumMessage
        {
            sndr = deviceId,
            inf = inf,
            trig = "usr"
        };

        await PublishAsync(_elementTopic, message);
    }

    // ------------------------------------------------------------------------
    // NEW: Publish game end (score)
    // ------------------------------------------------------------------------
    public async Task PublishGameEndAsync(string stage, int score)
    {
        var inf = new InfPayload
        {
            stage = stage,
            score = score
        };

        var message = new QuantumMessage
        {
            sndr = deviceId,
            inf = inf,
            trig = "time"
        };

        await PublishAsync(_elementTopic, message);
    }

    // ------------------------------------------------------------------------

    private async Task PublishAsync(string topic, QuantumMessage message)
    {
        if (_client == null || !_client.IsConnected)
        {
            Debug.LogWarning("[MQTT] Not connected, cannot publish");
            return;
        }

        string json = JsonConvert.SerializeObject(message);

        var appMsg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _client.PublishAsync(appMsg);
        Debug.Log($"[MQTT] TX {topic}: {json}");
    }

    private string GetLocalIPAddress()
    {
        try
        {
            string hostName = Dns.GetHostName();
            var host = Dns.GetHostEntry(hostName);
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }

        return "0.0.0.0";
    }

    private async void OnDestroy()
    {
        try
        {
            _cts?.Cancel();

            if (_reconnectTask != null)
                await _reconnectTask;
        }
        catch { }

        if (_client != null && _client.IsConnected)
        {
            try { await _client.DisconnectAsync(); }
            catch { }
        }

        _cts?.Dispose();
        _cts = null;
    }
}
