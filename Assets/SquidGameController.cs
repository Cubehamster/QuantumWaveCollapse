using System;
using System.Threading.Tasks;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.UniversalDelegates;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum SquidStage
{
    Idle,
    Intro,
    Active,   // was Playing
    Solved
}

public class SquidGameController : MonoBehaviour
{
    public static SquidGameController Instance { get; private set; }

    [Header("Start")]
    [SerializeField] private SquidStage startStage = SquidStage.Idle;

    [Header("Gameplay References")]
    [Tooltip("Main measurement / crosshair script (can be null if you wire it later).")]
    [SerializeField] private MeasurementClick measurementClick;

    [Tooltip("Root GameObject for Player 1 crosshair visuals (optional).")]
    [SerializeField] private GameObject crosshairP1Root;

    [Tooltip("Root GameObject for Player 2 crosshair visuals (optional).")]
    [SerializeField] private GameObject crosshairP2Root;

    [Header("Idle / Intro Settings")]
    [Tooltip("How often orbitals change while in Idle or Intro mode.")]
    [SerializeField] private float idleOrbitalInterval = 10f;

    [Header("Intro Tutorial UI")]
    [Tooltip("CanvasGroup for the mirrored 'Scan to Identify' text on Player 1 side.")]
    [SerializeField] private CanvasGroup scanP1Text;

    [Tooltip("CanvasGroup for the mirrored 'Scan to Identify' text on Player 2 side.")]
    [SerializeField] private CanvasGroup scanP2Text;

    [Tooltip("CanvasGroup for the 'Keep Green Particles' TextMeshPro.")]
    [SerializeField] private CanvasGroup keepGreenText;

    [Tooltip("CanvasGroup for the 'Destroy Red Particles' TextMeshPro.")]
    [SerializeField] private CanvasGroup destroyRedText;

    [Header("Intro Countdown UI")]
    [Tooltip("CanvasGroup for the overlay countdown (e.g., background + text).")]
    [SerializeField] private CanvasGroup countdownOverlay;

    [Tooltip("The TextMeshProUGUI that displays 'Get Ready', '3, 2, 1' etc.")]
    [SerializeField] private TextMeshProUGUI countdownLabel;
    [SerializeField] private TextMeshProUGUI countdownLabelFlipped;

    [SerializeField] private StabilityUI StabilityUI;

    [Header("Intro Timings")]
    [Tooltip("Warmup time AFTER both players have clicked/moved at least once.")]
    [SerializeField] private float introWarmupDuration = 3f;

    [Tooltip("How long to show 'Keep Green Particles' before resetting the Actual to Unknown.")]
    [SerializeField] private float introKeepGreenDuration = 6f;

    [Tooltip("Fade duration for tutorial texts.")]
    [SerializeField] private float introFadeDuration = 0.5f;

    [Tooltip("Length of the numeric countdown (3,2,1) after 'Get Ready'.")]
    [SerializeField] private float introCountdownDuration = 3f;

    [Header("Game Timers")]
    public CountdownTimer Timer;
    public CountdownTimer TimerFlipped;

    // MQTT / stage state
    private SquidMqttClient _mqtt;
    private SquidStage _currentStage;
    private int _hits;
    private int _misses;
    private float _timeLeft;
    private int _numPlayers = 1; // default until server tells us

    // Pending stage change requested from MQTT (background thread)
    // Applied on the main thread in Update()
    private bool _hasPendingStageFromServer;
    private SquidStage _pendingStageFromServer;

    public SquidStage CurrentStage => _currentStage;

    // Game-end tracking so we don’t finish twice
    private bool _gameEnded;

    // True once the main game timer has been started
    private bool _timerStarted;

    // Final game result
    private bool _finalStable;
    private float _finalScore;

    // Solved state coroutine handle
    private Coroutine _solvedRoutine;

    public Volume Darken;

    // ---------------- Intro tutorial internal state (runs inside Active) ----------------

    private enum IntroPhase
    {
        None,
        Warmup,                   // both players click/move at least once + 3s timer
        WaitingForFirstIdentify,  // first identify → forced Good
        AfterFirstIdentify,
        WaitingForSecondIdentify, // second identify → forced Bad
        AfterSecondIdentify,
        WaitingForDestroyBad,     // coop destroy Bad
        Countdown                 // showing Get Ready + 3,2,1
    }

    private IntroPhase _introPhase = IntroPhase.None;
    private Coroutine _introRoutine;

    // Warmup click/move tracking
    public bool _introP1Clicked;
    public bool _introP2Clicked;

    // Flags to force RNG (used by MeasurementClick)
    private bool _introForceNextGood;
    private bool _introForceNextBad;

    // Optional: skip tutorial on subsequent entries to Active
    private bool _introTutorialCompleted;

    public SquidMqttClient Mqtt;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private async void Start()
    {
        _mqtt = FindObjectOfType<SquidMqttClient>();
        if (measurementClick == null)
            measurementClick = FindObjectOfType<MeasurementClick>();

        _currentStage = startStage;

        // Apply initial local stage behaviour
        ApplyStageImmediately(_currentStage, sendMqtt: false);

        // Initial connection + stage report
        if (_mqtt != null)
        {
            await _mqtt.PublishConnectionAsync("connected");
            await SendStageUpdateAsync("connected");
        }
    }

    private void Update()
    {
        // ----------------------------------------------------
        // Apply any pending stage change from MQTT on main thread
        // ----------------------------------------------------
        if (_hasPendingStageFromServer)
        {
            _hasPendingStageFromServer = false;

            // Apply stage locally (no MQTT inside ApplyStageImmediately)
            ApplyStageImmediately(_pendingStageFromServer, sendMqtt: false);

            // Echo back to server that we've changed stage, with trig="server"
            if (_mqtt != null)
                _ = SendStageUpdateAsync("server");
        }

        // ----------------------------------------------------
        // Existing timer logic
        // ----------------------------------------------------
        if (_currentStage == SquidStage.Active && !_gameEnded && _timerStarted && Timer != null)
        {
            _timeLeft = Timer.remaining;

            if (_timeLeft <= 0f)
            {
                OnGameTimerFinished();
            }
        }

        DebugKeyStageSwitch();
    }


    // =========================================================
    //  MQTT callbacks & stage changes from server
    // =========================================================

    public async Task SetStageFromServer(string stageName)
    {
        if (!Enum.TryParse(stageName, true, out SquidStage newStage))
            return;

        // Just mark a pending change – don't touch ECS / EntityManager here.
        _pendingStageFromServer = newStage;
        _hasPendingStageFromServer = true;

        // Nothing else to do here; the actual ApplyStageImmediately + MQTT echo
        // will be done on the main thread in Update().
        await Task.CompletedTask;
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

        string stageString = _currentStage.ToString().ToLower();
        await _mqtt.PublishStageAsync(stageString, trig, _hits, _misses, _timeLeft);
    }

    // Game-end MQTT (per spec: trig = "time", score = 0/1)
    private async Task PublishGameEndAsync(int score)
    {
        if (_mqtt == null) return;

        // quant/squid {"sndr":"squid-ctrl", "inf":{"stage":"solved","score":score},"trig":"time"}
        await _mqtt.PublishGameEndAsync("solved", score);
    }

    // =========================================================
    //  Stage application (local behaviour)
    // =========================================================

    private void ApplyStageImmediately(SquidStage newStage, bool sendMqtt)
    {
        _currentStage = newStage;
        Debug.Log("[SQUID] Stage changed to " + _currentStage);

        // Stop any running intro sequence when stage changes away
        StopIntroIfAny();

        switch (_currentStage)
        {
            case SquidStage.Idle:
                EnterIdle();
            //    break;
            //case SquidStage.Intro:
            //    EnterIntro();
                break;
            case SquidStage.Active:
                EnterActive();
            //    break;
            //case SquidStage.Solved:
            //    EnterSolved();
                break;
        }

        if (sendMqtt && _mqtt != null)
        {
            _ = SendStageUpdateAsync("logic");
        }
    }

    private void EnterIdle()
    {
        ActualParticlePoolSystem.CurrentActive = 0;
        Darken.weight = 1.0f;
        Timer.remaining = 0;
        TimerFlipped.remaining = 0; 
        Timer.timerText.text = string.Empty;
        TimerFlipped.timerText.text = string.Empty;
        Debug.Log("[SQUID] EnterIdle");

        _gameEnded = false;
        _timerStarted = false;

        // Ensure chaos mode & forces are reset
        OrbitalPresetCycler.ExitChaosMode();
        ZeroAllForces();

        ActualParticlePoolSystem.RequestClearAll();

        SetMeasurementEnabled(false);
        MeasurementClick.ClickLocked = true;
        if (measurementClick != null)
            measurementClick.ClearClickRequests();

        SetCrosshairP1(false);
        SetCrosshairP2(false);

        if (StabilityUI != null)
        {
            StabilityUI.spawningEnabled = false;
            StabilityUI.FadeStability(0, 0.1f);
        }

        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();

        OrbitalPresetCycler.EnterIdleMode(idleOrbitalInterval);
    }

    private void EnterIntro()
    {
        Debug.Log("[SQUID] EnterIntro (practice)");
        Darken.weight = 0.0f;
        _gameEnded = false;
        _timerStarted = false;

        // Ensure chaos mode & forces are reset
        OrbitalPresetCycler.ExitChaosMode();
        ZeroAllForces();

        ActualParticlePoolSystem.RequestClearAll();

        SetMeasurementEnabled(true);
        MeasurementClick.ClickLocked = false;
        SetCrosshairP1(true);
        SetCrosshairP2(true);

        if (StabilityUI != null)
        {
            StabilityUI.spawningEnabled = false;
            StabilityUI.FadeStability(0, 0.1f);
        }

        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();

        OrbitalPresetCycler.EnterIdleMode(idleOrbitalInterval);
    }

    private void EnterActive()
    {
        Debug.Log("[SQUID] EnterActive");
        ActualParticlePoolSystem.CurrentActive = 0;
        Darken.weight = 0.0f;
        _gameEnded = false;
        _timerStarted = false;
        _introTutorialCompleted = false;

        // Ensure chaos is off and forces are clean
        OrbitalPresetCycler.ExitChaosMode();
        ZeroAllForces();

        // Stop idle orbital cycling, force a specific starting orbital for tutorial
        OrbitalPresetCycler.ExitIdleMode();
        OrbitalPresetCycler.ForcePreset("OneS");

        // Reset pool to a clean slate
        ActualParticlePoolSystem.RequestClearAll();

        // Measurement ON, both cursors visible
        SetMeasurementEnabled(true);
        MeasurementClick.ClickLocked = false;
        SetCrosshairP1(true);
        SetCrosshairP2(true);

        if (StabilityUI != null)
        {
            StabilityUI.spawningEnabled = false;
            StabilityUI.FadeStability(1, 2f);
        }

        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();

        _introForceNextGood = false;
        _introForceNextBad = false;
        _introPhase = IntroPhase.None;
        _introP1Clicked = false;
        _introP2Clicked = false;

        if (!_introTutorialCompleted)
        {
            _introRoutine = StartCoroutine(IntroRoutine());
        }
        else
        {
            // Skip tutorial on re-entry: start normal gameplay directly
            StartGameTimer();
            if (StabilityUI != null)
                StabilityUI.spawningEnabled = true;
        }
    }

    private void EnterSolved()
    {
        Debug.Log("[SQUID] EnterSolved");
        Darken.weight = 0.0f;
        _timerStarted = false;

        SetMeasurementEnabled(false);
        MeasurementClick.ClickLocked = true;
        if (measurementClick != null)
            measurementClick.ClearClickRequests();   // hard stop click input

        // Also zero ECS forces so no residual pushes
        ZeroAllForces();

        SetCrosshairP1(false);
        SetCrosshairP2(false);

        if (StabilityUI != null)
        {
            StabilityUI.spawningEnabled = false;
            StabilityUI.FadeStability(1, 2f);
        }

        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate(); // we’ll reuse overlay with new text

        if (_solvedRoutine != null)
            StopCoroutine(_solvedRoutine);

        _solvedRoutine = StartCoroutine(SolvedStateRoutine());
    }

    private System.Collections.IEnumerator SolvedStateRoutine()
    {
        // 1) Apply final particle state (all green or all red)
        ApplyFinalParticleState(_finalStable);

        Debug.Log("SolvedStateRoutine started. State is: " + _finalStable);

        // 2) Drive orbitals / chaos behaviour
        if (_finalStable)
        {
            // Stable result:
            //  - ensure chaos mode is OFF
            //  - snap back to OneS
            OrbitalPresetCycler.ExitChaosMode();
            OrbitalPresetCycler.ForcePreset("OneS");
        }
        else
        {
            Debug.Log("Enter ChaosMode State");
            // Unstable result:
            //  - turn on chaos mode (OrbitalKind2D.None + high diffusion)
            OrbitalPresetCycler.EnterChaosMode();
        }

        // 3) Show overlay message ("SYSTEM: STABLE/UNSTABLE")
        if (countdownOverlay != null)
        {
            countdownOverlay.gameObject.SetActive(true);
            yield return FadeCanvasGroup(countdownOverlay, 1f, 0.5f);
        }

        if (countdownLabel != null)
        {
            countdownLabel.text = _finalStable
                ? "SYSTEM STABALIZED"
                : "SYSTEM COLLAPSE";
            countdownLabelFlipped.text = _finalStable
                ? "SYSTEM STABALIZED"
                : "SYSTEM COLLAPSE";
        }

        // 4) Stay in Solved for 60 seconds
        const float solvedDuration = 60f;
        float t = 0f;
        while (t < solvedDuration && _currentStage == SquidStage.Solved)
        {
            t += Time.deltaTime;
            yield return null;
        }

        // 5) Fade out overlay
        if (countdownOverlay != null)
            yield return FadeCanvasGroup(countdownOverlay, 0f, 0.5f);

        // 6) Make sure chaos is OFF before next run, then back to Idle
        OrbitalPresetCycler.ExitChaosMode();

        ApplyStageImmediately(SquidStage.Idle, sendMqtt: false);
        _solvedRoutine = null;
    }

    private void ApplyFinalParticleState(bool stable)
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var em = world.EntityManager;
        var q = em.CreateEntityQuery(
            ComponentType.ReadWrite<ActualParticleSet>(),
            ComponentType.ReadWrite<ActualParticleStatusElement>(),
            ComponentType.ReadWrite<ActualParticleRef>()
        );

        if (q.IsEmptyIgnoreFilter)
            return;

        var cfgEnt = q.GetSingletonEntity();
        var statusBuf = em.GetBuffer<ActualParticleStatusElement>(cfgEnt);
        var refBuf = em.GetBuffer<ActualParticleRef>(cfgEnt);

        var targetStatus = stable ? ActualParticleStatus.Good
                                  : ActualParticleStatus.Bad;

        int activeCount = 0;

        for (int i = 0; i < statusBuf.Length && i < refBuf.Length; i++)
        {
            if (refBuf[i].Walker != Entity.Null)
            {
                statusBuf[i] = new ActualParticleStatusElement
                {
                    Value = targetStatus
                };
                activeCount++;
            }
        }

        // Keep the global counters consistent for UI
        ActualParticlePoolSystem.CurrentUnknown = 0;
        if (stable)
        {
            ActualParticlePoolSystem.CurrentGood = activeCount;
            ActualParticlePoolSystem.CurrentBad = 0;
        }
        else
        {
            ActualParticlePoolSystem.CurrentGood = 0;
            ActualParticlePoolSystem.CurrentBad = activeCount;
        }

        ActualParticlePoolSystem.CurrentActive = activeCount;
    }

    private void StopIntroIfAny()
    {
        if (_introRoutine != null)
        {
            StopCoroutine(_introRoutine);
            _introRoutine = null;
        }

        _introPhase = IntroPhase.None;
        _introForceNextGood = false;
        _introForceNextBad = false;
        _introP1Clicked = false;
        _introP2Clicked = false;
    }

    // =========================================================
    //  Game timer & end-of-game stability
    // =========================================================

    private float GetGameDurationSeconds()
    {
        switch (_numPlayers)
        {
            case 4: return 240f;
            case 3: return 180f;
            case 2: return 120f;
            default: return 120;
        }
    }

    private void StartGameTimer()
    {
        float duration = GetGameDurationSeconds();

        if (Timer != null)
            Timer.StartCountdown(duration);
        if (TimerFlipped != null)
            TimerFlipped.StartCountdown(duration);

        _timeLeft = duration;
        _gameEnded = false;
        _timerStarted = true;
    }

    private void OnGameTimerFinished()
    {
        _gameEnded = true;
        _timerStarted = false;

        // Compute score same way as StabilityUI
        int good = ActualParticlePoolSystem.CurrentGood;
        int bad = ActualParticlePoolSystem.CurrentBad;
        int unk = ActualParticlePoolSystem.CurrentActive - bad - good;

        int sum = bad + good + unk;
        float score = 0f;
        if (sum > 0)
        {
            score = ((float)good + 0.6f * unk + 0.1f * bad) / sum;
        }
        else
        {
            // No particles; treat as stable by default
            score = 1f;
        }

        int stableFlag = score >= 0.70f ? 1 : 0;

        _finalScore = score;
        _finalStable = (stableFlag == 1);

        Debug.Log($"[SQUID] Game timer finished. score={score:F3} -> stableFlag={stableFlag}");

        // Stop further interactions & spawns
        SetMeasurementEnabled(false);
        MeasurementClick.ClickLocked = true;
        if (measurementClick != null)
            measurementClick.ClearClickRequests();

        ZeroAllForces();

        SetCrosshairP1(false);
        SetCrosshairP2(false);

        if (StabilityUI != null)
            StabilityUI.spawningEnabled = false;

        // Move to solved stage (local)
        ApplyStageImmediately(SquidStage.Solved, sendMqtt: true);

        // MQTT game end message (score 0/1)
        _ = PublishGameEndAsync(stableFlag);
    }

    // =========================================================
    //  Intro tutorial sequence (runs while stage == Active)
    // =========================================================

    private System.Collections.IEnumerator IntroRoutine()
    {
        // Phase 0: Warmup.
        _introPhase = IntroPhase.Warmup;
        _introP1Clicked = false;
        _introP2Clicked = false;

        Debug.Log("[Intro] Warmup: waiting for both players to click/move at least once.");

        // Wait until both have interacted
        while (!(_introP1Clicked && _introP2Clicked) && _currentStage == SquidStage.Active)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Active)
            yield break;

        Debug.Log("[Intro] Both players have interacted. Warmup timer starts.");

        float warmupRemaining = introWarmupDuration;
        while (warmupRemaining > 0f && _currentStage == SquidStage.Active)
        {
            warmupRemaining -= Time.deltaTime;
            yield return null;
        }

        if (_currentStage != SquidStage.Active)
            yield break;

        // ---------------------------------------------------------
        // Phase 1: spawn 1 UNKNOWN Actual, disable P1, P2 scans to identify (forced Good)
        // ---------------------------------------------------------
        Debug.Log("[Intro] Phase 1: Spawn single UNKNOWN Actual, P1 disabled, P2 scans to identify (Good).");

        // Tutorial: no extra spawns from Good identify or coop destroy.
        ActualParticlePoolSystem.SetTutorialSpawnSuppression(
            suppressIdentify: true,
            suppressCoop: true
        );

        // Spawn exactly one UNKNOWN (no pre-set Bad status)
        ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);

        SetCrosshairP1(false);
        SetCrosshairP2(true);

        _introForceNextGood = true;  // first identification should become Good
        _introForceNextBad = false;
        _introPhase = IntroPhase.WaitingForFirstIdentify;

        // Fade in scan text
        yield return FadeCanvasGroup(scanP1Text, 1f, introFadeDuration);

        // Wait until OnActualIdentified() moves us on
        while (_currentStage == SquidStage.Active &&
               _introPhase == IntroPhase.WaitingForFirstIdentify)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Active)
            yield break;

        if (_introPhase != IntroPhase.AfterFirstIdentify)
            yield break;

        // ---------------------------------------------------------
        // Phase 2: show "Keep Green Particles"
        // ---------------------------------------------------------
        Debug.Log("[Intro] First Actual identified as GOOD. Showing 'Keep Green Particles'.");

        yield return FadeCanvasGroup(keepGreenText, 1f, introFadeDuration);
        SetMeasurementEnabled(false);
        SetCrosshairP2(false);
        yield return new WaitForSeconds(introKeepGreenDuration);

        // Turn any Good Actuals back to Unknown (tutorial-only behaviour)
        ForceAllGoodActualsToUnknown();

        yield return FadeCanvasGroup(keepGreenText, 0f, introFadeDuration);
        SetMeasurementEnabled(true);

        // Now swap to the other player
        Debug.Log("[Intro] Reset to Unknown, switching to other player.");

        SetCrosshairP1(true);
        SetCrosshairP2(false);

        _introForceNextGood = false;
        _introForceNextBad = true; // this identification becomes BAD
        _introPhase = IntroPhase.WaitingForSecondIdentify;

        // scan text for second player
        yield return FadeCanvasGroup(scanP2Text, 1f, introFadeDuration);

        // Wait for second identify
        while (_currentStage == SquidStage.Active &&
               _introPhase == IntroPhase.WaitingForSecondIdentify)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Active)
            yield break;

        if (_introPhase != IntroPhase.AfterSecondIdentify)
            yield break;

        // Now the Actual is BAD
        Debug.Log("[Intro] Second identify done (BAD). Enabling both cursors for co-op destroy.");

        // Hide "Scan to Identify" texts
        yield return FadeCanvasGroup(scanP1Text, 0f, introFadeDuration);
        yield return FadeCanvasGroup(scanP2Text, 0f, introFadeDuration);

        // Enable both crosshairs, show "Destroy Red Particles"
        SetCrosshairP1(true);
        SetCrosshairP2(true);

        _introPhase = IntroPhase.WaitingForDestroyBad;
        yield return FadeCanvasGroup(destroyRedText, 1f, introFadeDuration);

        // Wait until the BAD Actual is fully co-op scanned (destroyed)
        while (_currentStage == SquidStage.Active &&
               _introPhase == IntroPhase.WaitingForDestroyBad)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Active)
            yield break;

        if (_introPhase != IntroPhase.Countdown)
            yield break;

        // Phase 3: countdown
        Debug.Log("[Intro] BAD Actual destroyed. Starting countdown.");

        // Lock clicks during countdown (movement still allowed via MouseParty, but we block clicks)
        SetMeasurementEnabled(true);
        MeasurementClick.ClickLocked = true;

        // Hide "Destroy Red Particles"
        yield return FadeCanvasGroup(destroyRedText, 0f, introFadeDuration);

        // Countdown overlay
        yield return CountdownRoutine();

        // Tutorial done → normal Active gameplay
        _introPhase = IntroPhase.None;
        _introTutorialCompleted = true;

        // Re-enable measurement for normal gameplay
        SetMeasurementEnabled(true);
        MeasurementClick.ClickLocked = false;
        SetCrosshairP1(true);
        SetCrosshairP2(true);

        Debug.Log("[Intro] Tutorial complete. Continuing Active with normal rules.");
    }

    private System.Collections.IEnumerator CountdownRoutine()
    {
        // Players can move, but cannot click during countdown
        MeasurementClick.ClickLocked = true;

        if (countdownOverlay != null)
        {
            countdownOverlay.gameObject.SetActive(true);
            yield return FadeCanvasGroup(countdownOverlay, 1f, introFadeDuration);
        }

        // Step 1: "Get Ready"
        if (countdownLabel != null)
        {
            countdownLabel.text = "Get Ready";
            countdownLabelFlipped.text = "Get Ready";
        }


        yield return new WaitForSeconds(1f);

        // Step 2: 3, 2, 1
        for (int n = 3; n >= 1; n--)
        {
            if (countdownLabel != null)
            {
                countdownLabel.text = n.ToString();
                countdownLabelFlipped.text = n.ToString();
            }


            yield return new WaitForSeconds(1f);
        }

        // Clear label
        if (countdownLabel != null)
        {
            countdownLabel.text = string.Empty;
            countdownLabelFlipped.text = string.Empty;
        }


        // Fade out overlay
        if (countdownOverlay != null)
            yield return FadeCanvasGroup(countdownOverlay, 0f, introFadeDuration);

        // Tutorial finished:
        // - Allow pool spawns again
        // - Spawn the first two Unknown Actuals that start the real game
        ActualParticlePoolSystem.SetTutorialSpawnSuppression(
            suppressIdentify: false,
            suppressCoop: false);

        ActualParticlePoolSystem.RequestImmediateUnknownSpawns(2);

        // Unlock clicking so the normal game can proceed
        MeasurementClick.ClickLocked = false;

        // Start main game timer (based on _numPlayers)
        StartGameTimer();

        if (StabilityUI != null)
            StabilityUI.spawningEnabled = true;
    }

    // =========================================================
    //  Hooks from MeasurementClick / pool
    // =========================================================

    public void OnActualIdentified(int actualIndex, bool isGood)
    {
        if (_currentStage != SquidStage.Active)
            return;

        if (_introPhase == IntroPhase.WaitingForFirstIdentify)
        {
            _introForceNextGood = false;

            if (scanP1Text != null)
                StartCoroutine(FadeCanvasGroup(scanP1Text, 0f, introFadeDuration));
            if (scanP2Text != null)
                StartCoroutine(FadeCanvasGroup(scanP2Text, 0f, introFadeDuration));

            _introPhase = IntroPhase.AfterFirstIdentify;
            Debug.Log("[Intro] OnActualIdentified (first) index=" + actualIndex + " isGood=" + isGood);
        }
        else if (_introPhase == IntroPhase.WaitingForSecondIdentify)
        {
            _introForceNextBad = false;

            if (scanP1Text != null)
                StartCoroutine(FadeCanvasGroup(scanP1Text, 0f, introFadeDuration));
            if (scanP2Text != null)
                StartCoroutine(FadeCanvasGroup(scanP2Text, 0f, introFadeDuration));

            _introPhase = IntroPhase.AfterSecondIdentify;
            Debug.Log("[Intro] OnActualIdentified (second) index=" + actualIndex + " isGood=" + isGood);
        }
    }

    public void OnActualFullyScanned(int actualIndex, bool wasGood)
    {
        if (_currentStage != SquidStage.Active)
            return;

        if (_introPhase == IntroPhase.WaitingForDestroyBad && !wasGood)
        {
            _introPhase = IntroPhase.Countdown;
            Debug.Log("[Intro] OnActualFullyScanned BAD index=" + actualIndex + " -> starting countdown soon.");
        }
    }

    public bool TryConsumeIntroForcedIdentify(out bool isGood)
    {
        isGood = false;

        if (_currentStage != SquidStage.Active)
            return false;

        if (_introForceNextGood)
        {
            _introForceNextGood = false;
            isGood = true;
            return true;
        }

        if (_introForceNextBad)
        {
            _introForceNextBad = false;
            isGood = false;
            return true;
        }

        return false;
    }

    public void NotifyIntroClick(int playerIndex)
    {
        if (_currentStage != SquidStage.Active)
            return;

        if (_introPhase != IntroPhase.Warmup)
            return;

        if (playerIndex == 1)
            _introP1Clicked = true;
        else if (playerIndex == 2)
            _introP2Clicked = true;
    }

    // =========================================================
    //  Helpers: measurement & crosshairs
    // =========================================================

    private void SetMeasurementEnabled(bool enabled)
    {
        if (measurementClick != null)
            measurementClick.enabled = enabled;
    }

    private void SetCrosshairP1(bool enabled)
    {
        if (crosshairP1Root != null)
            crosshairP1Root.SetActive(enabled);

        // Also gate P1 input in MeasurementClick
        if (measurementClick != null)
            measurementClick.SetPlayer1InputEnabled(enabled);
    }

    private void SetCrosshairP2(bool enabled)
    {
        if (crosshairP2Root != null)
            crosshairP2Root.SetActive(enabled);

        // Also gate P2 input in MeasurementClick
        if (measurementClick != null)
            measurementClick.SetPlayer2InputEnabled(enabled);
    }

    // =========================================================
    //  Helpers: UI fades & status manipulation
    // =========================================================

    private void HideAllTutorialTextImmediate()
    {
        SetCanvasGroupImmediate(scanP1Text, 0f, false);
        SetCanvasGroupImmediate(scanP2Text, 0f, false);
        SetCanvasGroupImmediate(keepGreenText, 0f, false);
        SetCanvasGroupImmediate(destroyRedText, 0f, false);
    }

    private void ClearCountdownUIImmediate()
    {
        SetCanvasGroupImmediate(countdownOverlay, 0f, false);
        if (countdownLabel != null)
        {
            countdownLabel.text = string.Empty;
            countdownLabelFlipped.text = string.Empty;
        }

    }

    private void SetCanvasGroupImmediate(CanvasGroup cg, float alpha, bool active)
    {
        if (cg == null) return;
        cg.alpha = alpha;
        cg.gameObject.SetActive(active);
    }

    public void SetLanguageFromServer(string lang)
    {
        // lang will be "en" or "nl"
        Debug.Log("[SQUID] Language updated to: " + lang);

        // TODO: switch UI text content here
        // Example:
        // tutorialText.text = lang == "nl" ? "Scan om te identificeren" : "Scan to Identify";
    }

    private System.Collections.IEnumerator FadeCanvasGroup(CanvasGroup cg, float targetAlpha, float duration)
    {
        if (cg == null)
            yield break;

        cg.gameObject.SetActive(true);

        float startAlpha = cg.alpha;
        float t = 0f;

        if (duration <= 0f)
        {
            cg.alpha = targetAlpha;
        }
        else
        {
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                cg.alpha = Mathf.Lerp(startAlpha, targetAlpha, u);
                yield return null;
            }

            cg.alpha = targetAlpha;
        }

        if (Mathf.Approximately(targetAlpha, 0f))
            cg.gameObject.SetActive(false);
    }

    /// <summary>
    /// During intro: find any Good Actuals and set them back to Unknown.
    /// This keeps the visible walker but resets its status.
    /// </summary>
    private void ForceAllGoodActualsToUnknown()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var em = world.EntityManager;
        var q = em.CreateEntityQuery(
            ComponentType.ReadWrite<ActualParticleSet>(),
            ComponentType.ReadWrite<ActualParticleStatusElement>());

        if (q.IsEmptyIgnoreFilter)
            return;

        var cfgEnt = q.GetSingletonEntity();
        var statusBuf = em.GetBuffer<ActualParticleStatusElement>(cfgEnt);

        for (int i = 0; i < statusBuf.Length; i++)
        {
            if (statusBuf[i].Value == ActualParticleStatus.Good)
            {
                statusBuf[i] = new ActualParticleStatusElement
                {
                    Value = ActualParticleStatus.Unknown
                };
            }
        }
    }

    private void DebugKeyStageSwitch()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            Debug.Log("[DEBUG] Switched to Idle (1)");
            ApplyStageImmediately(SquidStage.Idle, sendMqtt: false);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            Debug.Log("[DEBUG] Switched to Intro (2)");
            ApplyStageImmediately(SquidStage.Intro, sendMqtt: false);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            Debug.Log("[DEBUG] Switched to Active (3)");
            ApplyStageImmediately(SquidStage.Active, sendMqtt: false);
        }
        else if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            Debug.Log("[DEBUG] Switched to Solved (4)");
            ApplyStageImmediately(SquidStage.Solved, sendMqtt: false);
        }
        else if (Input.GetKeyDown(KeyCode.F))
        {
            bool newValue = !measurementClick.playersFlipped;
            ApplyFlipPlayers(newValue);
            Debug.Log("[DEBUG] Toggled FlipPlayers → " + newValue);
        }
        else if (Input.GetKeyDown(KeyCode.Minus))
        {
            Mqtt.enabled = false;
            Mqtt.MQTTActive = false;
            Debug.Log("[DEBUG] MQTT → " + false);
        }
        else if (Input.GetKeyDown(KeyCode.Equals))
        {
            Mqtt.enabled = true;
            Mqtt.MQTTActive = true;
            Debug.Log("[DEBUG] MQTT → " + true);
        }
    }

    public void ApplyFlipPlayers(bool enable)
    {
        if (measurementClick == null)
            measurementClick = FindObjectOfType<MeasurementClick>();

        if (measurementClick == null)
        {
            Debug.LogWarning("[SQUID] Tried to flip players, but MeasurementClick is missing!");
            return;
        }

        // Only set if different, avoid unnecessary toggles
        if (measurementClick.playersFlipped != enable)
        {
            measurementClick.playersFlipped = enable;
            Debug.Log("[SQUID] Players flipped: " + enable);
        }
    }


    /// <summary>
    /// Hard reset all ECS Force components so cursors can't keep pushing
    /// after the game has ended / stage changed.
    /// </summary>
    private void ZeroAllForces()
    {
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null) return;

        var em = world.EntityManager;
        var q = em.CreateEntityQuery(ComponentType.ReadWrite<Force>());
        if (q.IsEmptyIgnoreFilter)
            return;

        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (var e in entities)
        {
            em.SetComponentData(e, new Force { Value = float2.zero });
        }
    }
}
