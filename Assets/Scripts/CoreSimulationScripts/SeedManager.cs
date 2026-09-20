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

    internal void HourlySeedUpdate()
    {
        using (var posPooled = GetPooledPositions())
        using (var removePooled = GetPooledIndices())
        {
            List<Vector3Int> activePos = posPooled.List;
            activePos.AddRange(activeSeeds.Keys);
            List<int> seedsToRemove = removePooled.List;

            foreach (var tilePos in activePos)
            {
                GrassInstance grass = null;
                // Check if grass instance already exists
                if (grassManager.activeGrassInstances.ContainsKey(tilePos))
                {
                    grass = grassManager.activeGrassInstances[tilePos];
                }

                List<Seed> seeds = activeSeeds[tilePos];
                seedsToRemove.Clear();
                for (int i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    if (seed.germinationStage < seed.sproutStage)
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
                        continue;
                    }
                    // Create grass instance if ready to sprout
                    if (seed.germinationStage >= seed.sproutStage)
                    {
                        
                        if (grass != null && !grass.IsDormant)
                        {
                            continue;
                        }

                        EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);

                        if (grass != null && grass.grassDensity < 0.6f)
                        {
                            grassManager.RemoveGrassInstance(tilePos, data);
                        }
                        else if (grass != null && grass.grassDensity >= 0.6f)
                        {
                            continue;
                        }
                        
                        seedsToRemove.Add(i);
                        // Germination chance related to energy consumed
                        if (Random.value > seed.germinationEnergy * (float)seed.sproutStage * 0.05f)
                        {
                            continue;
                        }
                        grassManager.SpawnNewGrass(data, tilePos, seed.carriedGenome, seed.storedEnergy, seed.parentGenus);
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
                List<Seed> seeds = activeSeeds[tilePos];
                seedsToRemove.Clear();
                for (int i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    // Seed decay
                    seed.storedEnergy -= 1f;
                    seeds[i] = seed;
                    // Check if energy runs out
                    if (seed.storedEnergy <= 0f)
                    {
                        seedsToRemove.Add(i);
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
                        seeds.RemoveAt(i);
                        DecrementSeedCount();
                    }
                }

                if (seeds.Count == 0)
                {
                    activeSeeds.Remove(tilePos);
                }
            }
        }
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
