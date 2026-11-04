using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public class SeedManager : MonoBehaviour
{
    internal Dictionary<Vector3Int, List<Seed>> activeSeeds = new Dictionary<Vector3Int, List<Seed>>();
    // [OPTIMIZED] Increased pool sizes for higher seed density scenarios
    private const int LIST_POOL_SIZE = 8;
    private ListPool<Vector3Int> positionListPool;
    private ListPool<int> indexListPool;
    internal long seedCount = 0;

    [Header("Seed Limits")]
    public int maxSeedsPerTile = 0; // 0 or lower means no limit

    [Header("References")]
    public MapManager mapManager;
    public GrassManager grassManager;

    void Awake()
    {
        if (SimulationStartupSettings.TryGet(out _, out _, out _, out _, out int startupMaxSeedsPerTile))
        {
            maxSeedsPerTile = startupMaxSeedsPerTile;
        }
    }

    void Start()
    {
        // [OPTIMIZED] Initialize pools with larger capacity for typical seed simulation loads
        positionListPool = new ListPool<Vector3Int>(LIST_POOL_SIZE, 16);
        indexListPool = new ListPool<int>(LIST_POOL_SIZE, 32);
    }

    private const float SEEDLING_DISPLACES_BELOW_SIZE = 0.6f; // A sprouting seed can replace a dormant plant smaller than this
    private const float SEEDLING_ENERGY_TO_GROW = 22f;        // A new plant's growth floor; more than this gives a seed no further edge

    [Header("Sprouting")]
    [Tooltip("Energy a germinated seed needs before it may sprout; weaker seeds keep waiting and decay. At 22 or more a seedling can start growing at once.")]
    [Min(0f)] public float minimumSproutEnergy = 15f;
    [Tooltip("How well a tile must suit a seed (0-1, the weaker of temperature and water fit) before it may sprout there.")]
    [Range(0f, 1f)] public float minimumSproutFit = 0.4f;

    /// <summary>
    /// A tile can take a seedling if it is free, or held by a small dormant plant the seedling can push out.
    /// </summary>
    internal static bool CanTakeSeedling(EnvironmentData data)
    {
        return data.grass == null || (data.grass.IsDormant && data.grass.grassDensity < SEEDLING_DISPLACES_BELOW_SIZE);
    }

    /// <summary>
    /// Habitat fit a seed waits for before germinating: its own cue, but never less than sprouting needs, so a seed never
    /// germinates in conditions it couldn't sprout in.
    /// </summary>
    internal float GerminationThreshold(Seed seed)
    {
        return Mathf.Max(seed.germinationCue, minimumSproutFit);
    }

    /// <summary>
    /// A plant just died here. Germinated seeds on the tile (the seedling bank) get first claim on the gap, as advance
    /// regeneration does in real stands; otherwise clones, which try every plant update, would nearly always fill it
    /// before the next germination step.
    /// </summary>
    internal void OnTileFreed(Vector3Int tilePos, EnvironmentData data)
    {
        if (!activeSeeds.TryGetValue(tilePos, out List<Seed> seeds))
            return;
        SproutBestSeed(tilePos, data, seeds);
        if (seeds.Count == 0)
        {
            activeSeeds.Remove(tilePos);
        }
    }

    [Header("Cold Preservation")]
    [Tooltip("Temperature (C) below which cold-preserving seeds start decaying more slowly.")]
    public float coldPreservationStartTemperature = 5f;
    [Tooltip("Degrees below the start temperature at which cold preservation reaches full strength.")]
    [Min(0.01f)] public float coldPreservationFullRange = 20f;

    /// <summary>
    /// One germination step. TimeController runs a fixed number of these per sim day, evenly spaced.
    /// </summary>
    internal void GerminationStep()
    {
        using (var posPooled = GetPooledPositions())
        using (var removePooled = GetPooledIndices())
        {
            List<Vector3Int> activePos = posPooled.List;
            activePos.AddRange(activeSeeds.Keys);
            List<int> seedsToRemove = removePooled.List;

            foreach (var tilePos in activePos)
            {
                EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);
                List<Seed> seeds = activeSeeds[tilePos];
                seedsToRemove.Clear();
                for (int i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    // Germinated seeds wait as a seedling bank for the tile to come free (see OnTileFreed)
                    if (seed.germinationStage >= seed.sproutStage)
                        continue;

                    // Seeds wait (without spending energy) until the habitat suits the plant they carry, at least as well as sprouting needs
                    if (GerminationSuitability(seed.carriedGenome, data) >= GerminationThreshold(seed))
                    {
                        // Consume energy to germinate
                        seed.storedEnergy -= seed.germinationEnergy;
                        seed.germinationStage++;
                        seeds[i] = seed;
                    }
                    // Check if energy runs out
                    if (seed.storedEnergy <= 0f)
                    {
                        seedsToRemove.Add(i);
                        DiscardSeed(data, seed);
                        ResearchCounters.Today.seedsExhausted++;
                    }
                }
                for (int j = seedsToRemove.Count - 1; j >= 0; j--)
                {
                    seeds.RemoveAt(seedsToRemove[j]);
                    DecrementSeedCount();
                }

                if (CanTakeSeedling(data))
                {
                    SproutBestSeed(tilePos, data, seeds);
                }

                if (seeds.Count == 0)
                {
                    activeSeeds.Remove(tilePos);
                }
            }
        }
    }

    /// <summary>
    /// Sprouts the germinated seed best placed to establish here: the best fit to the tile, with enough energy to start
    /// growing. Only seeds meeting minimumSproutEnergy and minimumSproutFit may sprout; the others keep waiting.
    /// Seeds that lose the germination roll are spent, and the next best tries; the rest keep waiting once one sprouts.
    /// </summary>
    private void SproutBestSeed(Vector3Int tilePos, EnvironmentData data, List<Seed> seeds)
    {
        while (true)
        {
            int best = -1;
            float bestScore = float.MinValue;
            for (int i = 0; i < seeds.Count; i++)
            {
                Seed candidate = seeds[i];
                if (candidate.germinationStage < candidate.sproutStage)
                    continue;

                float fit = GerminationSuitability(candidate.carriedGenome, data);
                if (fit < minimumSproutFit || candidate.storedEnergy < minimumSproutEnergy)
                    continue;

                float score = fit + Mathf.Clamp01(candidate.storedEnergy / SEEDLING_ENERGY_TO_GROW);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (best < 0)
                return;

            Seed seed = seeds[best];
            seeds.RemoveAt(best);
            DecrementSeedCount();

            // Germination chance related to energy consumed
            if (Random.value > seed.germinationEnergy * (float)seed.sproutStage * 0.05f)
            {
                ResearchCounters.Today.seedsFailedToSprout++;
                DiscardSeed(data, seed);
                continue;
            }

            if (data.grass != null)
            {
                grassManager.RemoveGrassInstance(tilePos, data, DeathCause.DisplacedBySeedling);
            }
            grassManager.genusTracker.RemoveSeed(seed.genusId); // Now a plant; SpawnNewGrass checks its genus again
            grassManager.SpawnNewGrass(data, tilePos, seed.carriedGenome, seed.storedEnergy, BirthCause.Seedling);
            data.grass.phosphorusReserve = seed.phosphorus;
            ResearchCounters.Today.seedsSprouted++;
            return;
        }
    }

    internal void SeedDecay()
    {
        using (var posPooled = GetPooledPositions())
        using (var removePooled = GetPooledIndices())
        {
            List<Vector3Int> activePos = posPooled.List;
            activePos.AddRange(activeSeeds.Keys);
            List<int> seedsToRemove = removePooled.List;

            foreach (var tilePos in activePos)
            {
                float coldness = Mathf.Clamp01((coldPreservationStartTemperature - mapManager.GetTileEnvironmentData(tilePos).temperature) / coldPreservationFullRange);
                List<Seed> seeds = activeSeeds[tilePos];
                seedsToRemove.Clear();
                for (int i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    // Seed decay, reduced in the cold for seeds with cold preservation
                    seed.storedEnergy -= 1f - Mathf.Clamp01(seed.coldPreservation) * coldness;
                    seeds[i] = seed;
                    // A germinated seed is a waiting seedling: once its reserves can no longer establish it, it dies
                    if (seed.germinationStage >= seed.sproutStage && seed.storedEnergy < minimumSproutEnergy)
                    {
                        seedsToRemove.Add(i);
                        DiscardSeed(mapManager.GetTileEnvironmentData(tilePos), seed);
                        ResearchCounters.Today.seedsDiedGerminated++;
                        continue;
                    }
                    // Check if energy runs out
                    if (seed.storedEnergy <= 0f)
                    {
                        seedsToRemove.Add(i);
                        DiscardSeed(mapManager.GetTileEnvironmentData(tilePos), seed);
                        ResearchCounters.Today.seedsDecayed++;
                        continue;
                    }
                }
                for (int j = seedsToRemove.Count - 1; j >= 0; j--)
                {
                    seeds.RemoveAt(seedsToRemove[j]);
                    DecrementSeedCount();
                }
                if (seeds.Count == 0)
                {
                    activeSeeds.Remove(tilePos);
                }
            }
        }

        PruneImpossibleSeeds();
    }

    internal void PruneImpossibleSeeds()
    {
        if (activeSeeds.Count == 0)
            return;

        using (var posPooled = GetPooledPositions())
        {
            List<Vector3Int> activePos = posPooled.List;
            activePos.AddRange(activeSeeds.Keys);
            foreach (var tilePos in activePos)
            {
                if (!activeSeeds.TryGetValue(tilePos, out List<Seed> seeds))
                    continue;

                for (int i = seeds.Count - 1; i >= 0; i--)
                {
                    Seed seed = seeds[i];
                    int remainingStages = seed.sproutStage - seed.germinationStage;
                    if (remainingStages < 0)
                    {
                        remainingStages = 0;
                    }
                    float requiredEnergy = seed.germinationEnergy * remainingStages;
                    if (requiredEnergy > seed.storedEnergy)
                    {
                        DiscardSeed(mapManager.GetTileEnvironmentData(tilePos), seed);
                        seeds.RemoveAt(i);
                        DecrementSeedCount();
                        ResearchCounters.Today.seedsPruned++;
                    }
                }

                if (seeds.Count == 0)
                {
                    activeSeeds.Remove(tilePos);
                }
            }
        }
    }

    /// <summary>
    /// How well a tile suits the plant a seed carries (0..1): the weaker of its temperature and hydration fit,
    /// using the same factors as photosynthesis, including too much water (temperature boost capped at 1).
    /// </summary>
    internal static float GerminationSuitability(PlantGenome genome, EnvironmentData data)
    {
        if (genome == null || data == null)
            return 0f;

        float hydrationRequirement = Mathf.Max(0.0001f, genome.GetTraitValue(TraitId.HydrationRequirement));
        float hydrationScore = Mathf.Clamp01(data.hydration / hydrationRequirement);
        if (data.hydration > hydrationRequirement)
        {
            hydrationScore = Mathf.Clamp01(hydrationScore - (data.hydration - hydrationRequirement) / 80f);
        }
        float temperatureScore = Mathf.Clamp01(GrassInstance.TemperatureMultiplier(data.temperature, genome.GetTraitValue(TraitId.TemperatureTolerance)));
        return Mathf.Min(hydrationScore, temperatureScore);
    }

    private PooledList<Vector3Int> GetPooledPositions()
    {
        if (positionListPool == null)
        {
            positionListPool = new ListPool<Vector3Int>(LIST_POOL_SIZE, LIST_POOL_SIZE);
        }
        return positionListPool.Get();
    }

    private PooledList<int> GetPooledIndices()
    {
        if (indexListPool == null)
        {
            indexListPool = new ListPool<int>(LIST_POOL_SIZE, LIST_POOL_SIZE);
        }
        return indexListPool.Get();
    }

    /// <summary>
    /// A dead seed rots where it lies, returning its phosphorus to that tile, and leaves its genus's seed count.
    /// </summary>
    private void DiscardSeed(EnvironmentData tile, Seed seed)
    {
        tile.availablePhosphorus += seed.phosphorus;
        grassManager.genusTracker.RemoveSeed(seed.genusId);
    }

    private void DecrementSeedCount()
    {
        if (seedCount > 0)
        {
            seedCount -= 1;
        }
    }

    internal bool TryAddSeed(Vector3Int tilePos, Seed seed)
    {
        if (!activeSeeds.TryGetValue(tilePos, out List<Seed> seeds))
        {
            seeds = new List<Seed>();
            activeSeeds[tilePos] = seeds;
        }

        if (!CanAddSeedToTile(seeds))
        {
            return false;
        }

        seeds.Add(seed);
        seedCount += 1;
        return true;
    }

    private bool CanAddSeedToTile(List<Seed> seeds)
    {
        return maxSeedsPerTile <= 0 || seeds.Count < maxSeedsPerTile;
    }

    public void SetMaxSeedsPerTile(int newLimit)
    {
        maxSeedsPerTile = Mathf.Max(0, newLimit);
        TrimSeedsToMaxPerTile();
    }

    private void TrimSeedsToMaxPerTile()
    {
        if (maxSeedsPerTile <= 0 || activeSeeds.Count == 0)
            return;

        using (var posPooled = GetPooledPositions())
        {
            List<Vector3Int> activePos = posPooled.List;
            activePos.AddRange(activeSeeds.Keys);
            foreach (Vector3Int tilePos in activePos)
            {
                if (!activeSeeds.TryGetValue(tilePos, out List<Seed> seeds))
                    continue;

                while (seeds.Count > maxSeedsPerTile)
                {
                    DiscardSeed(mapManager.GetTileEnvironmentData(tilePos), seeds[seeds.Count - 1]);
                    seeds.RemoveAt(seeds.Count - 1);
                    DecrementSeedCount();
                }

                if (seeds.Count == 0)
                {
                    activeSeeds.Remove(tilePos);
                }
            }
        }
    }
}
