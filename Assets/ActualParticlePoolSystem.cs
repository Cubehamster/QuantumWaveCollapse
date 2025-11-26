using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Per-slot metadata for Actual particles.
/// Age = seconds since the slot became active with a non-null Walker.
/// </summary>
public struct ActualParticleMetaElement : IBufferElementData
{
    public float Age;
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct ActualParticlePoolSystem : ISystem
{
    struct ReturnRequest
    {
        public int Index;
        public bool WasGood;
    }

    // Pending spawn count: how many *new* Unknown actuals we want to spawn.
    static int s_PendingSpawnCount;

    // Slots to return (free) after full co-op scan
    static List<ReturnRequest> s_ReturnRequests;

    // --- Spawn cooldown (seconds) ---
    public static float SpawnCooldownDuration = 0.2f;
    static float s_SpawnCooldown;

    // --- Fade / scan lock timings (seconds) ---
    // These are used by BOTH UI and MeasurementSystem.
    // New / reused slots:
    //   - Age < InvisibleDelay      -> fully invisible, not scannable
    //   - InvisibleDelay–FadeInEnd  -> fade-in alpha, still not scannable
    //   - Age >= ScanLockDuration   -> fully visible & scannable
    public static float InvisibleDelay = 0.5f;
    public static float FadeInDuration = 0.5f;
    public static float ScanLockDuration => InvisibleDelay + FadeInDuration;

    // --- Public counters (status counts across the pool) ---
    public static int CurrentUnknown;
    public static int CurrentGood;
    public static int CurrentBad;

    // --- Orbital cycling based on spawns ---
    static int s_SpawnedSinceLastOrbit;

    /// <summary>
    /// Call when an actual just got identified (Unknown -> Good/Bad).
    /// - If isGood == true: spawn +1 new Unknown from the pool (after cooldown).
    /// - If isGood == false: no extra spawn here.
    /// </summary>
    public static void NotifyIdentified(int actualIndex, bool isGood)
    {
        if (isGood)
        {
            s_PendingSpawnCount += 1;
            s_SpawnCooldown = Mathf.Max(s_SpawnCooldown, SpawnCooldownDuration);
        }
    }

    /// <summary>
    /// Call when an actual has been fully co-op scanned ("destroyed").
    /// - That slot is freed.
    /// - If wasGood: spawn +1 new Unknown.
    /// - If wasBad:  spawn +2 new Unknown.
    /// </summary>
    public static void NotifyFullyScanned(int actualIndex, bool wasGood)
    {
        if (s_ReturnRequests == null)
            s_ReturnRequests = new List<ReturnRequest>();

        s_ReturnRequests.Add(new ReturnRequest
        {
            Index = actualIndex,
            WasGood = wasGood
        });

        s_PendingSpawnCount += wasGood ? 1 : 2;
        s_SpawnCooldown = Mathf.Max(s_SpawnCooldown, SpawnCooldownDuration);
    }

    EntityQuery _cfgQ;
    EntityQuery _walkersQ;

    // Track whether we've done the initial DesiredCount spawn
    bool _didInitialSpawn;

    public void OnCreate(ref SystemState state)
    {
        _cfgQ = state.GetEntityQuery(
            ComponentType.ReadWrite<ActualParticleSet>(),
            ComponentType.ReadWrite<ActualParticleRng>(),
            ComponentType.ReadWrite<ActualParticleRef>(),
            ComponentType.ReadWrite<ActualParticlePositionElement>(),
            ComponentType.ReadWrite<ActualParticleStatusElement>(),
            ComponentType.ReadWrite<ActualParticleMetaElement>());

        _walkersQ = state.GetEntityQuery(
            ComponentType.ReadOnly<ParticleTag>(),
            ComponentType.ReadOnly<Position>());

        _didInitialSpawn = false;
    }

    public void OnDestroy(ref SystemState state)
    {
        // nothing special
    }

    public void OnUpdate(ref SystemState state)
    {
        var em = state.EntityManager;

        if (_cfgQ.IsEmptyIgnoreFilter)
            return;
        if (_walkersQ.IsEmptyIgnoreFilter)
            return;

        float dt = SystemAPI.Time.DeltaTime;

        // Tick cooldown
        if (s_SpawnCooldown > 0f)
        {
            s_SpawnCooldown -= dt;
            if (s_SpawnCooldown < 0f)
                s_SpawnCooldown = 0f;
        }

        var cfgEnt = _cfgQ.GetSingletonEntity();

        var set = em.GetComponentData<ActualParticleSet>(cfgEnt);
        var rng = em.GetComponentData<ActualParticleRng>(cfgEnt);
        var refBuf = em.GetBuffer<ActualParticleRef>(cfgEnt);
        var posBuf = em.GetBuffer<ActualParticlePositionElement>(cfgEnt);
        var statusBuf = em.GetBuffer<ActualParticleStatusElement>(cfgEnt);
        var metaBuf = em.GetBuffer<ActualParticleMetaElement>(cfgEnt);

        // ---------------------------------------------------------
        // Ensure pool size & buffer lengths
        // ---------------------------------------------------------
        int poolSize = set.PoolSize > 0
            ? set.PoolSize
            : math.max(1, set.DesiredCount);

        int oldLen = refBuf.Length;
        if (oldLen != poolSize)
        {
            refBuf.ResizeUninitialized(poolSize);
            posBuf.ResizeUninitialized(poolSize);
            statusBuf.ResizeUninitialized(poolSize);
            metaBuf.ResizeUninitialized(poolSize);

            for (int i = oldLen; i < poolSize; i++)
            {
                refBuf[i] = new ActualParticleRef { Walker = Entity.Null };
                posBuf[i] = new ActualParticlePositionElement { Value = float2.zero };
                statusBuf[i] = new ActualParticleStatusElement { Value = ActualParticleStatus.Unknown };
                metaBuf[i] = new ActualParticleMetaElement { Age = 0f };
            }
        }

        // ---------------------------------------------------------
        // INITIAL SPAWN: once at startup
        // ---------------------------------------------------------
        if (!_didInitialSpawn)
        {
            int initial = math.clamp(set.DesiredCount, 0, poolSize);
            s_PendingSpawnCount += initial;
            _didInitialSpawn = true;
        }

        // ---------------------------------------------------------
        // Handle pending returns (full co-op scans)
        // ---------------------------------------------------------
        if (s_ReturnRequests != null && s_ReturnRequests.Count > 0)
        {
            for (int i = 0; i < s_ReturnRequests.Count; i++)
            {
                var rq = s_ReturnRequests[i];
                int idx = rq.Index;
                if (idx < 0 || idx >= refBuf.Length)
                    continue;

                // Free this slot & reset its status/meta
                refBuf[idx] = new ActualParticleRef { Walker = Entity.Null };
                posBuf[idx] = new ActualParticlePositionElement { Value = float2.zero };
                statusBuf[idx] = new ActualParticleStatusElement { Value = ActualParticleStatus.Unknown };
                metaBuf[idx] = new ActualParticleMetaElement { Age = 0f };
            }

            s_ReturnRequests.Clear();
        }

        // ---------------------------------------------------------
        // Spawn requested number of new Unknown actuals (after cooldown)
        // ---------------------------------------------------------
        int spawnedThisFrame = 0;

        if (s_PendingSpawnCount > 0 && s_SpawnCooldown <= 0f)
        {
            using var walkers = _walkersQ.ToEntityArray(Allocator.Temp);

            if (walkers.Length > 0)
            {
                // Track walkers already used by any pool slot
                var used = new NativeHashSet<Entity>(refBuf.Length, Allocator.Temp);
                for (int i = 0; i < refBuf.Length; i++)
                {
                    if (refBuf[i].Walker != Entity.Null)
                        used.Add(refBuf[i].Walker);
                }

                int maxToSpawn = s_PendingSpawnCount;

                for (int s = 0; s < maxToSpawn; s++)
                {
                    int freeSlot = FindFreeSlot(refBuf);
                    if (freeSlot < 0)
                        break; // pool is full

                    Entity chosen = PickRandomUnusedWalker(walkers, ref rng, used);
                    if (chosen == Entity.Null)
                        break; // no free walker left

                    refBuf[freeSlot] = new ActualParticleRef
                    {
                        Walker = chosen
                    };
                    statusBuf[freeSlot] = new ActualParticleStatusElement
                    {
                        Value = ActualParticleStatus.Unknown
                    };
                    // Position will be synced by your position system.
                    metaBuf[freeSlot] = new ActualParticleMetaElement
                    {
                        Age = 0f // start invisible, then fade in
                    };

                    used.Add(chosen);
                    spawnedThisFrame++;
                }

                used.Dispose();

                s_PendingSpawnCount -= spawnedThisFrame;
                if (s_PendingSpawnCount < 0)
                    s_PendingSpawnCount = 0;
            }
        }

        // ---------------------------------------------------------
        // Trigger orbital changes based on spawn count
        // ---------------------------------------------------------
        if (spawnedThisFrame > 0)
        {
            s_SpawnedSinceLastOrbit += spawnedThisFrame;

            // Every 5 spawned Actuals → random new orbital (unseen in current cycle)
            if (s_SpawnedSinceLastOrbit >= 5)
            {
                s_SpawnedSinceLastOrbit = 0;
                OrbitalPresetCycler.RequestRandomStepFromSpawns();
            }
        }

        // ---------------------------------------------------------
        // Advance per-slot Age for *active* slots
        // ---------------------------------------------------------
        for (int i = 0; i < refBuf.Length && i < metaBuf.Length; i++)
        {
            if (refBuf[i].Walker != Entity.Null)
            {
                var m = metaBuf[i];
                m.Age += dt;
                metaBuf[i] = m;
            }
            else
            {
                // Inactive slot; keep Age at 0 so next spawn starts clean.
                metaBuf[i] = new ActualParticleMetaElement { Age = 0f };
            }
        }

        // ---------------------------------------------------------
        // Count active slots + per-status counts
        // ---------------------------------------------------------
        int activeCount = 0;
        int unk = 0, good = 0, bad = 0;

        for (int i = 0; i < refBuf.Length; i++)
        {
            if (refBuf[i].Walker != Entity.Null)
                activeCount++;
        }

        for (int i = 0; i < statusBuf.Length; i++)
        {
            var st = statusBuf[i].Value;
            switch (st)
            {
                case ActualParticleStatus.Good: good++; break;
                case ActualParticleStatus.Bad: bad++; break;
                default: unk++; break;
            }
        }

        set.TargetActive = activeCount;

        CurrentUnknown = unk;
        CurrentGood = good;
        CurrentBad = bad;

        // ---------------------------------------------------------
        // Write back config + RNG
        // ---------------------------------------------------------
        em.SetComponentData(cfgEnt, set);
        em.SetComponentData(cfgEnt, rng);
    }

    // -------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------

    static int FindFreeSlot(DynamicBuffer<ActualParticleRef> refs)
    {
        for (int i = 0; i < refs.Length; i++)
        {
            if (refs[i].Walker == Entity.Null)
                return i;
        }
        return -1;
    }

    static Entity PickRandomUnusedWalker(
        NativeArray<Entity> walkers,
        ref ActualParticleRng rng,
        NativeHashSet<Entity> used)
    {
        int total = walkers.Length;
        if (total == 0)
            return Entity.Null;

        // xorshift32 RNG
        static uint NextUInt(ref uint state)
        {
            uint x = state;
            if (x == 0u) x = 1u;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        uint stateVal = rng.Value;

        const int maxTries = 64;
        for (int attempt = 0; attempt < maxTries; attempt++)
        {
            int idx = (int)(NextUInt(ref stateVal) % (uint)total);
            var candidate = walkers[idx];
            if (!used.Contains(candidate))
            {
                rng.Value = stateVal;
                return candidate;
            }
        }

        // Fallback: linear scan
        for (int i = 0; i < total; i++)
        {
            if (!used.Contains(walkers[i]))
            {
                rng.Value = stateVal;
                return walkers[i];
            }
        }

        rng.Value = stateVal;
        return Entity.Null;
    }
}
