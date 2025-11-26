using System.Threading.Tasks;
using UnityEngine;

public enum SquidStage
{
    Idle,
    Intro,
    Playing,
    Failed,
    Solved
}

public class SquidGameController : MonoBehaviour
{
    public static SquidGameController Instance { get; private set; }

    [SerializeField] private SquidStage startStage = SquidStage.Idle;

    private SquidMqttClient _mqtt;
    private SquidStage _currentStage;
    private int _hits;
    private int _misses;
    private float _timeLeft;
    private int _numPlayers = 1;  // default until server tells us

    private void Awake()
    {
        Instance = this;
    }

    private async void Start()
    {
        _mqtt = FindObjectOfType<SquidMqttClient>();
        _currentStage = startStage;

        // Initial connection + stage report
        if (_mqtt != null)
        {
            await _mqtt.PublishConnectionAsync("connected");
            await SendStageUpdateAsync("logic");
        }
    }

    public async Task SetStageFromServer(string stageName)
    {
        // called by SquidMqttClient when it receives set.stage
        if (!System.Enum.TryParse(stageName, true, out SquidStage newStage))
            return;

        _currentStage = newStage;
        Debug.Log("[SQUID] Stage set from server: " + _currentStage);

        // Any game-side behaviour:
        switch (_currentStage)
        {
            case SquidStage.Idle:
                // reset puzzle visuals etc.
                break;
            case SquidStage.Intro:
                // play intro animation / audio
                break;
            case SquidStage.Playing:
                // start timer, enable shooting
                break;
            case SquidStage.Failed:
                // show fail feedback
                break;
            case SquidStage.Solved:
                // victory sequence
                break;
        }

        if (_mqtt != null)
            await SendStageUpdateAsync("server");
    }

    public async Task ApplySettingsFromServer(float? timer, int? difficulty, int? numplayers)
    {
        if (numplayers.HasValue)
        {
            _numPlayers = numplayers.Value;
            Debug.Log("[SQUID] numplayers set to " + _numPlayers);
        }

        if (timer.HasValue)
        {
            _timeLeft = timer.Value;
            Debug.Log("[SQUID] Timer set to " + _timeLeft);
        }

        // difficulty can affect target speed, etc.

        if (_mqtt != null)
            await SendStageUpdateAsync("server");
    }

    public async Task RegisterHit()
    {
        _hits++;
        if (_mqtt != null)
            await _mqtt.PublishUserInputAsync(_currentStage.ToString().ToLower(), "hit");

        await SendStageUpdateAsync("usr");
    }

    public async Task RegisterMiss()
    {
        _misses++;
        if (_mqtt != null)
            await _mqtt.PublishUserInputAsync(_currentStage.ToString().ToLower(), "miss");

        await SendStageUpdateAsync("usr");
    }

    private async Task SendStageUpdateAsync(string trig)
    {
        if (_mqtt == null) return;

        string stageString = _currentStage.ToString().ToLower(); // e.g. "idle", "playing"

        await _mqtt.PublishStageAsync(stageString, trig, _hits, _misses, _timeLeft);
    }
}
