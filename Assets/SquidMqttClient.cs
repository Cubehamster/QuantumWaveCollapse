using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using UnityEngine;

public class SquidMqttClient : MonoBehaviour
{
    [Header("MQTT Broker")]
    [SerializeField] private string brokerIp = "10.0.0.10";
    [SerializeField] private int brokerPort = 1883;

    [Header("Device Info")]
    [SerializeField] private string deviceId = "squid-ctrl";
    [SerializeField] private string version = "1.0.0";

    private IMqttClient _client;
    private string _elementTopic = "quant/squid";
    private string _rootTopic = "quant";

    private async void Start()
    {
        await ConnectAndSubscribeAsync();
    }

    private async Task ConnectAndSubscribeAsync()
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

        var options = new MqttClientOptionsBuilder()
            .WithClientId(deviceId)
            .WithTcpServer(brokerIp, brokerPort)
            .WithCleanSession()
            .Build();

        try
        {
            await _client.ConnectAsync(options);
            Debug.Log("[MQTT] Connected to broker " + brokerIp + ":" + brokerPort);

            // quant/squid/#
            await _client.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic(_elementTopic + "/#")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build());

            // quant/#
            await _client.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic(_rootTopic + "/#")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build());

            Debug.Log("[MQTT] Subscribed to quant/squid/# and quant/#");

            await PublishConnectionAsync("connected");
        }
        catch (Exception ex)
        {
            Debug.LogError("[MQTT] Connection failed: " + ex.Message);
        }
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
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
                return Task.CompletedTask; // ignore own messages

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
    }


    // ---- Public methods you call from gameplay code ----

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
        if (_client != null && _client.IsConnected)
        {
            await _client.DisconnectAsync();
        }
    }
}
