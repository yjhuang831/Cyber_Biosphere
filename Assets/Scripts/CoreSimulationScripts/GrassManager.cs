using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public class GrassManager : MonoBehaviour
{
    [Header("References")]
    public MapManager mapManager;
    public EnvironmentManager environmentManager;
    public GeneManager geneManager;
    public SeedManager seedManager;
    public SeasonManager seasonManager;

    [Header("Plant Factors")]
    public float metabolismCostPerAllele = 0.01f;
    public bool plantAging = true;
    public float agingRate = 0.1f; // Rate at which grass ages, affecting energy and density over time
    public float baseGrowthCost = 0.5f; // Energy cost per metabolic cycle for density growth
    public int flowerLimit = 1; // Max flowering events per month to prevent over-flowering and encourage diversity
    [Tooltip("Clone attempts each plant may make per sim day. 0 = unlimited (one attempt per plant update, the original behaviour).")]
    [Min(0)] public int maxCloneAttemptsPerDay = 0;

    // Collection of active grass instances for individual updates
    internal Dictionary<Vector3Int, GrassInstance> activeGrassInstances = new Dictionary<Vector3Int, GrassInstance>();
    
    // [OPTIMIZED] Position list pool - reduces allocation pressure during update loops
    private const int LIST_POS_POOL_SIZE = 8; // [OPTIMIZED] Increased pool size for high-grass-density scenarios
    private ListPool<Vector3Int> positionListPool;
    private const long MAX_PLANT_COUNTER = 1_000_000_000L;
    internal long plantCount = 0;

    void Start()
    {
        // [OPTIMIZED] Initialize pool with larger capacity for typical simulation
        positionListPool = new ListPool<Vector3Int>(LIST_POS_POOL_SIZE, 16);
    }

    internal void UpdateAllGrassInstances(float simTime)
    {
        using (var pooled = GetPooledPositions())
        {
            List<Vector3Int> activePositions = pooled.List;
            activePositions.AddRange(activeGrassInstances.Keys);

            foreach (var tilePos in activePositions) // Use pooled list to avoid allocation during iteration
            {
                // Check if the grass still exists (may have been removed)
                if (!activeGrassInstances.TryGetValue(tilePos, out GrassInstance grassInstance))
                    continue;

                // Get the corresponding environment data
                EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);
                
                CheckGrassState(tilePos, data); // Ensure grass state is updated before metabolism

                if (data.grass == null)
                    continue; // Grass may have died during state check

                if (TimeController.accumulatedAge > 0)
                {
                    grassInstance.age = IncrementWithWrap(grassInstance.age);
                    if (grassInstance.IsFlowering)
                    {
                        grassInstance.floweringDays = IncrementWithWrap(grassInstance.floweringDays);
                    }
                    grassInstance.ApplyDailyAgeing(plantAging, agingRate, TimeController.fullDayLength);

                    // After the day's ageing (which reflects yesterday's state), decide dormancy from yesterday's daylight
                    grassInstance.DailyDormancyDecision();
                }

                grassInstance.PerformMetabolism(data, metabolismCostPerAllele, simTime, baseGrowthCost, TimeController.isNight);

                // Check if grass should reproduce (handles energy deduction internally)
                if (!grassInstance.IsDormant && grassInstance.CheckAndHandleSpread() && TryUseCloneAttempt(grassInstance))
                {
                    SpreadIndividualGrass(tilePos, data);
                }
                if (grassInstance.floweringDays > 0 && !grassInstance.IsFlowering)
                {
                    grassInstance.floweringDays = 0;
                }
                // Self-pollination is rolled once per day, on the same daily tick as pollen spread
                if (TimeController.accumulatedAge > 0 && grassInstance.IsFlowering && grassInstance.floweringDays > 0)
                {
                    SelfPollinate(grassInstance, tilePos);
                }
                bool startedFloweringThisTick = false;
                // Check if grass should flower
                if (!grassInstance.IsDormant && !grassInstance.IsFlowering && !grassInstance.IsSeeding && grassInstance.CheckFlowering())
                {
                    startedFloweringThisTick = Flower(grassInstance, tilePos);
                    if (startedFloweringThisTick)
                    {
                        SpreadPollen(grassInstance, tilePos);
                    }
                }
                // Spread pollen once per day while flowering
                if (TimeController.accumulatedAge > 0 && grassInstance.IsFlowering && !startedFloweringThisTick)
                {
                    SpreadPollen(grassInstance, tilePos);
                }
                // Check if seed is ready
                if (grassInstance.IsSeeding && grassInstance.seedingEnergy >= grassInstance.genome.GetTraitValue(TraitId.SeedEnergyThreshold))
                {
                    DisperseSeed(grassInstance, tilePos);
                }
            }
        }

        if (TimeController.accumulatedAge > 0)
        {
            TimeController.accumulatedAge -= 1; // Decrement accumulated age after applying aging effects to grass instances
        }
    }

    /// <summary>
    /// Enforces maxCloneAttemptsPerDay. An attempt counts even if the chosen neighbour turns out to be occupied.
    /// </summary>
    private bool TryUseCloneAttempt(GrassInstance grass)
    {
        long today = TimeController.simDaysSinceStart;
        if (grass.lastCloneDay != today)
        {
            grass.lastCloneDay = today;
            grass.cloneAttemptsToday = 0;
        }

        if (maxCloneAttemptsPerDay > 0 && grass.cloneAttemptsToday >= maxCloneAttemptsPerDay)
        {
            if (grass.cloneAttemptsToday == maxCloneAttemptsPerDay)
            {
                // Count each capped plant once per day; the bump past the limit marks it as already counted
                ResearchCounters.Today.cloneAttemptsCapped++;
                grass.cloneAttemptsToday++;
            }
            return false;
        }

        grass.cloneAttemptsToday++;
        ResearchCounters.Today.cloneAttempts++;
        return true;
    }

    internal void SpreadIndividualGrass(Vector3Int origin, EnvironmentData originData)
    {
        PlantGenome parentGenome = originData.grass.genome;

        if (originData.immediateNeighbors.Count == 0)
            return;

        int randomIndex = Random.Range(0, originData.immediateNeighbors.Count);
        Vector3Int spreadPos = originData.immediateNeighbors[randomIndex];

        EnvironmentData targetData = mapManager.GetTileEnvironmentData(spreadPos);

        if (targetData.grass == null)
        {
            // Create genome from the DNA and check for speciation
            PlantGenome clonedGenome = geneManager.CloneGrass(parentGenome, originData.radiation);
            // Run AssignGrass() to simplify the process
            AssignGrass(targetData, spreadPos, clonedGenome, originData);
        }
    }

    internal void AssignGrass(EnvironmentData targetData, Vector3Int gridposition, PlantGenome grass, EnvironmentData originData)
    {
        if (targetData.grass == null)
        {
            float spreadCost = originData.grass.genome.GetTraitValue(TraitId.SpreadCost);

            targetData.grass = new GrassInstance(gridposition, grass, spreadCost); // Start with energy equal to spread cost for new spreads
            originData.grass.energyStored -= spreadCost; // Deduct energy cost from parent if spreading

            // Register the grass instance for updates
            activeGrassInstances[gridposition] = targetData.grass;
            plantCount = IncrementWithWrap(plantCount);
            ResearchCounters.RecordBirth(BirthCause.Clone);

            mapManager.UpdateGrassTile(targetData, gridposition);
        }
    }

    internal void SpawnNewGrass(EnvironmentData targetData, Vector3Int gridposition, PlantGenome genome = null, float iniEnergy = 0f, string parentGenus = null, BirthCause cause = BirthCause.Player)
    {
        ResearchCounters.RecordBirth(cause);
        bool hadGrass = targetData.grass != null;
        PlantGenome newGenome = null;
        bool isSavedSpecies = false;
        
        if (genome == null)
        {
            newGenome = geneManager.GenerateNewGenome(targetData, true);
            isSavedSpecies = false;
        }
        else
        {
            newGenome = genome;
            isSavedSpecies = true;
        }

        targetData.grass = new GrassInstance(gridposition, newGenome);
        if (genome != null)
        {
            targetData.grass.energyStored = iniEnergy;
        }
        // Register the grass instance for updates
        activeGrassInstances[gridposition] = targetData.grass;
        if (!hadGrass)
        {
            plantCount = IncrementWithWrap(plantCount);
        }
        mapManager.UpdateGrassTile(targetData, gridposition);
    }

    /// <summary>
    /// Spawns grass on a specific tile using a provided runtime genome.
    /// Returns false if the tile is invalid, occupied, or required managers are missing.
    /// </summary>
    public bool SpawnGrassAtTile(Vector3Int tilePosition, PlantGenome genome, float initialEnergy = 0f, string parentGenus = null)
    {
        if (mapManager == null)
        {
            Debug.LogWarning("SpawnGrassAtTile failed: MapManager reference is missing.");
            return false;
        }

        if (!mapManager.IsValidTilePosition(tilePosition))
        {
            return false;
        }

        EnvironmentData targetData = mapManager.GetTileEnvironmentData(tilePosition);
        if (targetData == null || targetData.grass != null)
        {
            return false;
        }

        if (genome == null)
        {
            Debug.LogWarning("SpawnGrassAtTile failed: genome is null.");
            return false;
        }

        targetData.grass = new GrassInstance(tilePosition, genome);
        targetData.grass.energyStored = initialEnergy;
        activeGrassInstances[tilePosition] = targetData.grass;
        plantCount = IncrementWithWrap(plantCount);
        ResearchCounters.RecordBirth(BirthCause.Player);
        mapManager.UpdateGrassTile(targetData, tilePosition);
        return true;
    }

    /// <summary>
    /// Converts a saved JSON genome payload to a runtime PlantGenome.
    /// Optional genus/species overrides are applied when provided.
    /// </summary>
    internal PlantGenome BuildGenomeFromState(PlantGenomeState source, string genusOverride = null, string speciesOverride = null)
    {
        if (source == null)
        {
            return null;
        }

        bool hasMaternal = source.maternalDNA != null && source.maternalDNA.Count > 0;
        bool hasPaternal = source.paternalDNA != null && source.paternalDNA.Count > 0;
        if (!hasMaternal && !hasPaternal)
        {
            return null;
        }

        string resolvedGenus = string.IsNullOrWhiteSpace(genusOverride) ? source.genus : genusOverride.Trim();
        string resolvedSpecies = string.IsNullOrWhiteSpace(speciesOverride) ? source.species : speciesOverride.Trim();
        string resolvedOrigin = string.IsNullOrWhiteSpace(source.origin) ? resolvedSpecies : source.origin.Trim();

        if (string.IsNullOrWhiteSpace(resolvedGenus) && string.IsNullOrWhiteSpace(resolvedSpecies) && string.IsNullOrWhiteSpace(resolvedOrigin))
        {
            return null;
        }

        PlantGenome genome = new PlantGenome
        {
            genus = resolvedGenus,
            species = resolvedSpecies,
            origin = resolvedOrigin,
            speciesGuid = string.IsNullOrWhiteSpace(source.speciesGuid) ? null : source.speciesGuid,
            generation = 0,  // All spawned genomes start at generation 0
            genusColor = source.genusColor != null
                ? new Color(source.genusColor.r, source.genusColor.g, source.genusColor.b, source.genusColor.a)
                : Color.white,
            maternalDNA = DeserializeAlleles(source.maternalDNA),
            paternalDNA = DeserializeAlleles(source.paternalDNA)
        };

        bool useMaternalDominant = source.dominantDNA != 1;
        genome.SetDominantDNA(useMaternalDominant);

        if (geneManager != null)
        {
            geneManager.RebuildLoadedGenomeDerivedData(genome);
        }
        else
        {
            genome.phenotypes = new float[TraitId.Count];
        }

        return genome;
    }

    /// <summary>
    /// Pushes a library species rename onto genomes living in this simulation (plants, pending seed genomes, seeds).
    /// Founders spawned from the library carry the names; their pure lineage carries the old epithet as origin tag.
    /// Hybrid origin tags ("A × B") are historical records and are left untouched.
    /// </summary>
    internal void ApplySpeciesRename(string speciesGuid, string oldSpecies, string newGenus, string newSpecies)
    {
        if (string.IsNullOrEmpty(speciesGuid))
            return;

        foreach (GrassInstance grass in activeGrassInstances.Values)
        {
            RenameGenome(grass.genome, speciesGuid, oldSpecies, newGenus, newSpecies);
            RenameGenome(grass.seedGenome, speciesGuid, oldSpecies, newGenus, newSpecies);
        }

        if (seedManager != null)
        {
            foreach (List<Seed> seeds in seedManager.activeSeeds.Values)
            {
                for (int i = 0; i < seeds.Count; i++)
                {
                    RenameGenome(seeds[i].carriedGenome, speciesGuid, oldSpecies, newGenus, newSpecies);
                }
            }
        }
    }

    /// <summary>
    /// Makes a living plant the founding specimen of a newly saved library species, using the same convention as
    /// plants spawned from the library (names set, origin tag = epithet). The plant gets its own genome copy so
    /// genetically identical siblings sharing the genome object are not swept into the species.
    /// </summary>
    internal void AssignSpecimenToSpecies(GrassInstance grass, string speciesGuid, string genus, string species)
    {
        if (grass == null || grass.genome == null || string.IsNullOrEmpty(speciesGuid))
            return;

        PlantGenome specimen = grass.genome.DeepCopy();
        specimen.speciesGuid = speciesGuid;
        specimen.genus = genus;
        specimen.species = species;
        specimen.origin = species;
        grass.genome = specimen;
    }

    private static void RenameGenome(PlantGenome genome, string speciesGuid, string oldSpecies, string newGenus, string newSpecies)
    {
        if (genome == null || genome.speciesGuid != speciesGuid)
            return;

        if (!string.IsNullOrWhiteSpace(genome.genus) || !string.IsNullOrWhiteSpace(genome.species))
        {
            genome.genus = newGenus;
            genome.species = newSpecies;
        }
        if (genome.origin == oldSpecies)
        {
            genome.origin = newSpecies;
        }
    }

    private static List<Allele> DeserializeAlleles(List<AlleleState> source)
    {
        if (source == null || source.Count == 0)
        {
            return new List<Allele>();
        }

        List<Allele> result = new List<Allele>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            AlleleState a = source[i];
            result.Add(new Allele(a.value, a.dominance, a.traitId));
        }
        return result;
    }

    public void RemoveGrassInstance(Vector3Int tilePos, EnvironmentData data)
    {
        RemoveGrassInstance(tilePos, data, DeathCause.Other);
    }

    internal void RemoveGrassInstance(Vector3Int tilePos, EnvironmentData data, DeathCause cause)
    {
        activeGrassInstances.Remove(tilePos);

        if (data.grass != null)
        {
            ResearchCounters.RecordDeath(cause);
            data.grass.Die(); // Handle any cleanup in the GrassInstance itself
            if (plantCount > 0)
            {
                plantCount -= 1;
            }
        }
        data.grass = null; // Clear grass from environment data
        mapManager.UpdateGrassTile(data, tilePos);
    }

    internal void CheckGrassState(Vector3Int tilePos, EnvironmentData data)
    {
        if (data.grass != null)
        {
            if (!data.grass.IsAlive())
            {
                // Grass died (starvation, dormancy timeout, or withering)
                RemoveGrassInstance(tilePos, data, data.grass.GetDeathCause());
            }
            else
            {
                // Grass is alive - update grass tile only if density or type changed
                mapManager.UpdateGrassTile(data, tilePos);
            }
        }
        else
        {
            // No grass - only clear if not already clear (avoid redundant calls)
            mapManager.UpdateGrassTile(data, tilePos);
        }
    }

    private bool Flower(GrassInstance grass, Vector3Int grassPos)
    {
        int daysPerMonth = Mathf.Max(1, seasonManager.DAYS_PER_MONTH);
        long currentMonth = TimeController.simDaysSinceStart / daysPerMonth;
        if (grass.lastFlowerMonth != currentMonth)
        {
            grass.lastFlowerMonth = currentMonth;
            grass.flowersThisMonth = 0;
        }
        if (grass.flowersThisMonth >= flowerLimit)
        {
            return false;
        }

        grass.reproductiveState = ReproductiveState.Flowering;
        grass.flowersThisMonth = IncrementWithWrap(grass.flowersThisMonth);
        ResearchCounters.Today.floweringStarts++;
        return true;
    }

    private void SpreadPollen(GrassInstance grass, Vector3Int grassPos)
    {
        if (grass == null || !grass.IsFlowering)
        {
            return;
        }

        int range = (int)grass.genome.GetTraitValue(TraitId.PollenRange); // Pollen range

        // Precompute pollen gamete once; all pollen from this grass is identical for this event
        var pollenGamete = geneManager.Meiosis(grass.genome, mapManager.GetTileEnvironmentData(grassPos).radiation);

        // Reservoir-sample up to 7 receiver tiles to avoid full shuffle
        const int maxTargets = 7;
        using (var pooledRange = GetPooledPositions())
        using (var pooled = GetPooledPositions())
        {
            List<Vector3Int> possibleTiles = pooledRange.List;
            mapManager.GetTilePosOfRange(grassPos, range, possibleTiles);
            List<Vector3Int> chosen = pooled.List;
            int seen = 0;
            foreach (var tile in possibleTiles)
            {
                if (seen < maxTargets)
                {
                    chosen.Add(tile);
                }
                else
                {
                    int j = Random.Range(0, seen + 1);
                    if (j < maxTargets)
                    {
                        chosen[j] = tile;
                    }
                }
                seen++;
            }

            foreach (var tile in chosen)
            {
                if (!mapManager.IsValidTilePosition(tile))
                    continue;
                if (!activeGrassInstances.TryGetValue(tile, out var targetGrass))
                    continue;
                if (!targetGrass.IsFlowering)
                    continue;

                // Manhattan distance success falloff
                int dist = Mathf.Abs(tile.x - grassPos.x) + Mathf.Abs(tile.y - grassPos.y);
                float maxSuccess = 0.95f;
                float success = range > 0 ? maxSuccess * Mathf.Clamp01(1f - (float)dist / range) : maxSuccess;

                if (Random.value <= success)
                {
                    EnvironmentData receiverData = mapManager.GetTileEnvironmentData(tile);
                    float localRadiation = receiverData.radiation;

                    var maternalGamete = geneManager.Meiosis(targetGrass.genome, localRadiation);
                    float traitCompositionDivergence = CalculateTraitCompositionDivergence(maternalGamete, pollenGamete);
                    float compatibilityFactor = Mathf.Clamp01(1f - traitCompositionDivergence);
                    float adjustedSuccess = success * compatibilityFactor;

                    if (Random.value <= adjustedSuccess)
                    {
                        PlantGenome seedGenome = geneManager.GenerateSeedGenome(
                            maternalGamete,
                            pollenGamete,
                            targetGrass.genome.generation,
                            grass.genome.generation,
                            targetGrass.genome.origin,
                            grass.genome.origin,
                            targetGrass.genome.speciesGuid,
                            grass.genome.speciesGuid);

                        targetGrass.reproductiveState = ReproductiveState.Seeding;
                        targetGrass.seedGenome = seedGenome;
                        ResearchCounters.Today.crossPollinations++;
                    }
                }
            }
        }
    }

    // [OPTIMIZED] Reused per-trait locus count buffer (left minus right) for divergence checks
    private int[] traitCountDelta = new int[32];

    private float CalculateTraitCompositionDivergence(List<Allele> left, List<Allele> right)
    {
        if (left == null || right == null)
        {
            return 0f;
        }

        int maxTraitId = -1;
        for (int i = 0; i < left.Count; i++)
            maxTraitId = Mathf.Max(maxTraitId, left[i].traitId);
        for (int i = 0; i < right.Count; i++)
            maxTraitId = Mathf.Max(maxTraitId, right[i].traitId);
        if (maxTraitId >= traitCountDelta.Length)
        {
            traitCountDelta = new int[maxTraitId + 1];
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].traitId >= 0)
                traitCountDelta[left[i].traitId]++;
        }
        for (int i = 0; i < right.Count; i++)
        {
            if (right[i].traitId >= 0)
                traitCountDelta[right[i].traitId]--;
        }

        int totalAlleles = left.Count + right.Count;
        float divergence = 0f;
        for (int t = 0; t <= maxTraitId; t++)
        {
            divergence += Mathf.Abs(traitCountDelta[t]);
            traitCountDelta[t] = 0; // leave the buffer clean for the next call
        }

        return totalAlleles > 0 ? divergence / totalAlleles : 0f;
    }

    private void SelfPollinate(GrassInstance parent, Vector3Int parentPos)
    {
        if (Random.value <= parent.floweringDays * 0.2f)
        {
            if (Random.value > 0.5f)
            {
                parent.reproductiveState = ReproductiveState.None;
                ResearchCounters.Today.selfPollinationFailures++;
                return; // 50% chance to fail self-pollination
            }
            var firstGamete = geneManager.Meiosis(parent.genome, mapManager.GetTileEnvironmentData(parentPos).radiation);
            var secondGamete = geneManager.Meiosis(parent.genome, mapManager.GetTileEnvironmentData(parentPos).radiation);

            PlantGenome seedGenome = geneManager.GenerateSeedGenome(
                firstGamete,
                secondGamete,
                parent.genome.generation,
                parent.genome.generation,
                parent.genome.origin,
                parent.genome.origin,
                parent.genome.speciesGuid,
                parent.genome.speciesGuid);

            parent.reproductiveState = ReproductiveState.Seeding;
            parent.seedGenome = seedGenome;
            ResearchCounters.Today.selfPollinations++;
        }
    }

    /// <summary>
    /// Disperse seeds from a seeding grass. Releases SeedCount seeds that split the stored seeding energy evenly;
    /// each lands on a random tile within the Manhattan dispersal range (several may share a tile).
    /// Seeds landing off-map or on a full tile are lost along with their energy.
    /// </summary>
    private void DisperseSeed(GrassInstance parent, Vector3Int parentPos)
    {
        if (parent == null || parent.genome == null) return;

        parent.reproductiveState = ReproductiveState.None;

        int range = Mathf.Max(1, (int)parent.genome.GetTraitValue(TraitId.SeedDispersalRange)); // reuse trait 21 for seed dispersal radius
        int seedCount = Mathf.Max(1, Mathf.RoundToInt(parent.genome.GetTraitValue(TraitId.SeedCount)));
        float energyPerSeed = parent.seedingEnergy / seedCount;
        // Seed provisioning traits come from the mother, like germination energy and sprout stage
        float germinationEnergy = parent.genome.GetTraitValue(TraitId.GerminationEnergy);
        byte sproutStage = (byte)parent.genome.GetTraitValue(TraitId.SproutStage);
        float germinationCue = parent.genome.GetTraitValue(TraitId.GerminationCue);
        float coldPreservation = parent.genome.GetTraitValue(TraitId.SeedColdPreservation);

        ResearchCounters.Today.seedDispersals++;
        ResearchCounters.Today.seedsReleased += seedCount;
        for (int i = 0; i < seedCount; i++)
        {
            Vector3Int tile = parentPos + RandomOffsetInManhattanRange(range);
            if (!mapManager.IsValidTilePosition(tile))
            {
                ResearchCounters.Today.seedsLost++;
                continue;
            }

            bool added = seedManager.TryAddSeed(tile, new Seed
            {
                carriedGenome = parent.seedGenome,
                parentGenus = parent.genome.genus,
                storedEnergy = energyPerSeed,
                germinationEnergy = germinationEnergy,
                germinationStage = 0,
                sproutStage = sproutStage,
                germinationCue = germinationCue,
                coldPreservation = coldPreservation
            });
            if (!added)
            {
                ResearchCounters.Today.seedsLost++; // Tile already at the seed cap
            }
        }
        parent.seedingEnergy = 0f;
        parent.seedGenome = null;
    }

    /// <summary>
    /// Uniform random offset with |dx| + |dy| <= range (rejection sampling; acceptance is ~50%).
    /// </summary>
    private static Vector3Int RandomOffsetInManhattanRange(int range)
    {
        while (true)
        {
            int dx = Random.Range(-range, range + 1);
            int dy = Random.Range(-range, range + 1);
            if (Mathf.Abs(dx) + Mathf.Abs(dy) <= range)
                return new Vector3Int(dx, dy, 0);
        }
    }

    internal PooledList<Vector3Int> GetPooledPositions()
    {
        if (positionListPool == null)
        {
            positionListPool = new ListPool<Vector3Int>(LIST_POS_POOL_SIZE, LIST_POS_POOL_SIZE);
        }
        return positionListPool.Get();
    }

    private static long IncrementWithWrap(long value)
    {
        value++;
        if (value >= MAX_PLANT_COUNTER)
        {
            return 0;
        }
        return value;
    }

    public void SetMetabolismCostPerAllele(float newCost)
    {
        metabolismCostPerAllele = Mathf.Max(0f, newCost);
    }

    public void SetPlantAging(bool enabled)
    {
        plantAging = enabled;
    }

    public void SetAgingRate(float newRate)
    {
        agingRate = Mathf.Max(0f, newRate);
    }

    public void SetBaseGrowthCost(float newCost)
    {
        baseGrowthCost = Mathf.Max(0f, newCost);
    }

    public void SetFlowerLimit(int newLimit)
    {
        flowerLimit = Mathf.Max(1, newLimit);
    }

}
