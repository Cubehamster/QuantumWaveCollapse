using System;
using System.Threading.Tasks;
using TMPro;
using Unity.Entities;
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

    public CountdownTimer Timer;
    public CountdownTimer TimerFlipped;

    // MQTT / stage state
    private SquidMqttClient _mqtt;
    private SquidStage _currentStage;
    private int _hits;
    private int _misses;
    private float _timeLeft;
    private int _numPlayers = 1; // default until server tells us

    public SquidStage CurrentStage => _currentStage;

    // ---------------- Intro tutorial internal state (runs inside Playing) ----------------

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

    // Optional: skip tutorial on subsequent entries to Playing
    private bool _introTutorialCompleted;

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
            await SendStageUpdateAsync("logic");
        }
    }

    // =========================================================
    //  MQTT callbacks & stage changes from server
    // =========================================================

    public async Task SetStageFromServer(string stageName)
    {
        if (!Enum.TryParse(stageName, true, out SquidStage newStage))
            return;

        ApplyStageImmediately(newStage, sendMqtt: false);

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

    // =========================================================
    //  Stage application (local behaviour)
    // =========================================================

    private void ApplyStageImmediately(SquidStage newStage, bool sendMqtt)
    {
        //// We allow re-entering Playing (tutorial) so no early-return there.
        //if (newStage == _currentStage && newStage != SquidStage.Playing)
        //    return;

        _currentStage = newStage;
        Debug.Log("[SQUID] Stage changed to " + _currentStage);

        // Stop any running intro sequence when stage changes away
        StopIntroIfAny();

        switch (_currentStage)
        {
            case SquidStage.Idle:
                EnterIdle();
                break;
            case SquidStage.Intro:
                EnterIntro();
                break;
            case SquidStage.Playing:
                EnterPlaying();
                break;
            case SquidStage.Failed:
                EnterFailed();
                break;
            case SquidStage.Solved:
                EnterSolved();
                break;
        }

        if (sendMqtt && _mqtt != null)
        {
            _ = SendStageUpdateAsync("logic");
        }
    }

    private void EnterIdle()
    {
        Debug.Log("[SQUID] EnterIdle");

        ActualParticlePoolSystem.RequestClearAll();

        SetMeasurementEnabled(false);
        SetCrosshairP1(false);
        SetCrosshairP2(false);
        StabilityUI.FadeStability(0, 0.1f);

        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();

        OrbitalPresetCycler.EnterIdleMode(idleOrbitalInterval);
    }

    /// <summary>
    /// New Intro: like Idle, but crosshairs ON so players can move/feel them.
    /// No Actuals in the pool; orbitals idle-cycle.
    /// </summary>
    private void EnterIntro()
    {
        Debug.Log("[SQUID] EnterIntro (practice)");

        ActualParticlePoolSystem.RequestClearAll();

        SetMeasurementEnabled(true);
        SetCrosshairP1(true);
        SetCrosshairP2(true);
        StabilityUI.FadeStability(0, 0.1f);

        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();

        OrbitalPresetCycler.EnterIdleMode(idleOrbitalInterval);
    }

    /// <summary>
    /// Playing now includes the tutorial sequence first, then transitions
    /// seamlessly into normal gameplay (same stage = Playing).
    /// </summary>
    private void EnterPlaying()
    {
        Debug.Log("[SQUID] EnterPlaying");

        // Stop idle orbital cycling, force a specific starting orbital for tutorial
        OrbitalPresetCycler.ExitIdleMode();
        OrbitalPresetCycler.ForcePreset("OneS");

        // Reset pool to a clean slate
        ActualParticlePoolSystem.RequestClearAll();

        // Measurement ON, both cursors visible
        SetMeasurementEnabled(true);
        SetCrosshairP1(true);
        SetCrosshairP2(true);
        StabilityUI.FadeStability(1, 2f);

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
            // If you want to skip tutorial entirely on re-entry, this is where
            // you’d start normal gameplay logic directly.
        }
    }

    private void EnterFailed()
    {
        Debug.Log("[SQUID] EnterFailed");

        SetMeasurementEnabled(false);
        SetCrosshairP1(false);
        SetCrosshairP2(false);
        StabilityUI.FadeStability(1, 2f);

        ActualParticlePoolSystem.RequestClearAll();
        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();
    }

    private void EnterSolved()
    {
        Debug.Log("[SQUID] EnterSolved");

        SetMeasurementEnabled(false);
        SetCrosshairP1(false);
        SetCrosshairP2(false);
        StabilityUI.FadeStability(1, 2f);

        ActualParticlePoolSystem.RequestClearAll();
        HideAllTutorialTextImmediate();
        ClearCountdownUIImmediate();
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
    //  Intro tutorial sequence (runs while stage == Playing)
    // =========================================================

    private System.Collections.IEnumerator IntroRoutine()
    {
        // Phase 0: Warmup.
        // Both players must click or move once; AFTER that, we wait introWarmupDuration seconds.
        _introPhase = IntroPhase.Warmup;
        _introP1Clicked = false;
        _introP2Clicked = false;

        Debug.Log("[Intro] Warmup: waiting for both players to click/move at least once.");

        // Wait until both have interacted
        while (!(_introP1Clicked && _introP2Clicked) && _currentStage == SquidStage.Playing)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Playing)
            yield break;

        Debug.Log("[Intro] Both players have interacted. Warmup timer starts.");

        float warmupRemaining = introWarmupDuration;
        while (warmupRemaining > 0f && _currentStage == SquidStage.Playing)
        {
            warmupRemaining -= Time.deltaTime;
            yield return null;
        }

        if (_currentStage != SquidStage.Playing)
            yield break;

        // ---------------------------------------------------------
        // Phase 1: spawn 1 UNKNOWN Actual, disable P1, P2 scans to identify (forced Good)
        // ---------------------------------------------------------
        Debug.Log("[Intro] Phase 1: Spawn single UNKNOWN Actual, P1 disabled, P2 scans to identify (Good).");

        // IMPORTANT: do NOT call RequestClearAll here, or it will zero out
        // the pending spawn on the next ECS update.
        // The pool was already cleared in EnterPlaying().

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

        // Fade in both scan texts (each side sees their mirrored version)
        yield return FadeCanvasGroup(scanP1Text, 1f, introFadeDuration);
        //yield return FadeCanvasGroup(scanP2Text, 1f, introFadeDuration);

        // Wait until OnActualIdentified() moves us on
        while (_currentStage == SquidStage.Playing &&
               _introPhase == IntroPhase.WaitingForFirstIdentify)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Playing)
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

        // Show scan texts again (for Player 1 this time in terms of meaning)
        //yield return FadeCanvasGroup(scanP1Text, 1f, introFadeDuration);
        yield return FadeCanvasGroup(scanP2Text, 1f, introFadeDuration);

        // Wait for second identify
        while (_currentStage == SquidStage.Playing &&
               _introPhase == IntroPhase.WaitingForSecondIdentify)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Playing)
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
        while (_currentStage == SquidStage.Playing &&
               _introPhase == IntroPhase.WaitingForDestroyBad)
        {
            yield return null;
        }

        if (_currentStage != SquidStage.Playing)
            yield break;

        if (_introPhase != IntroPhase.Countdown)
            yield break;

        // Phase 3: countdown (no new Actuals spawned from coop destroy—
        // suppression is still ON at this point).
        Debug.Log("[Intro] BAD Actual destroyed. Starting countdown.");

        // Lock scanning during countdown (movement still allowed via ClickLocked)
        SetMeasurementEnabled(true);

        // Hide "Destroy Red Particles"
        yield return FadeCanvasGroup(destroyRedText, 0f, introFadeDuration);

        // Countdown overlay
        yield return CountdownRoutine();

        // Tutorial done → normal Playing
        _introPhase = IntroPhase.None;
        _introTutorialCompleted = true;

        // Re-enable measurement for normal gameplay
        SetMeasurementEnabled(true);
        SetCrosshairP1(true);
        SetCrosshairP2(true);

        Debug.Log("[Intro] Tutorial complete. Continuing Playing with normal rules.");
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
            countdownLabel.text = "Get Ready";

        // show "Get Ready" for 1 second (tweak as you like)
        yield return new WaitForSeconds(1f);

        // Step 2: 3, 2, 1
        for (int n = 3; n >= 1; n--)
        {
            if (countdownLabel != null)
                countdownLabel.text = n.ToString();

            // You can use introCountdownDuration / 3f if you want a different pacing,
            // but here we just use 1 second per number.
            yield return new WaitForSeconds(1f);
        }

        // Clear label
        if (countdownLabel != null)
            countdownLabel.text = string.Empty;

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

        Timer.StartCountdown(120);
        TimerFlipped.StartCountdown(120);
    }

    // =========================================================
    //  Hooks from MeasurementClick / pool
    // =========================================================

    /// <summary>
    /// MeasurementClick calls this when an Actual is identified (Unknown -> Good/Bad).
    /// </summary>
    public void OnActualIdentified(int actualIndex, bool isGood)
    {
        if (_currentStage != SquidStage.Playing)
            return;

        // First identify (should be Good)
        if (_introPhase == IntroPhase.WaitingForFirstIdentify)
        {
            _introForceNextGood = false;

            // Fade out scan texts
            if (scanP1Text != null)
                StartCoroutine(FadeCanvasGroup(scanP1Text, 0f, introFadeDuration));
            if (scanP2Text != null)
                StartCoroutine(FadeCanvasGroup(scanP2Text, 0f, introFadeDuration));

            _introPhase = IntroPhase.AfterFirstIdentify;
            Debug.Log("[Intro] OnActualIdentified (first) index=" + actualIndex + " isGood=" + isGood);
        }
        // Second identify (should be Bad)
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

    /// <summary>
    /// Called by MeasurementClick when an Actual has been fully co-op scanned (destroyed).
    /// </summary>
    public void OnActualFullyScanned(int actualIndex, bool wasGood)
    {
        if (_currentStage != SquidStage.Playing)
            return;

        // We only care about the final BAD destruction for the tutorial
        if (_introPhase == IntroPhase.WaitingForDestroyBad && !wasGood)
        {
            _introPhase = IntroPhase.Countdown;
            Debug.Log("[Intro] OnActualFullyScanned BAD index=" + actualIndex + " -> starting countdown soon.");
        }
    }

    /// <summary>
    /// MeasurementClick can call this in OnProgressComplete to override RNG
    /// during the intro tutorial. Returns true if a forced result was consumed.
    /// </summary>
    public bool TryConsumeIntroForcedIdentify(out bool isGood)
    {
        isGood = false;

        if (_currentStage != SquidStage.Playing)
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

    /// <summary>
    /// Called from MeasurementClick when a player clicks or moves during tutorial warmup.
    /// </summary>
    public void NotifyIntroClick(int playerIndex)
    {
        if (_currentStage != SquidStage.Playing)
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
    }

    private void SetCrosshairP2(bool enabled)
    {
        if (crosshairP2Root != null)
            crosshairP2Root.SetActive(enabled);
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
            countdownLabel.text = string.Empty;
    }

    private void SetCanvasGroupImmediate(CanvasGroup cg, float alpha, bool active)
    {
        if (cg == null) return;
        cg.alpha = alpha;
        cg.gameObject.SetActive(active);
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
}
