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
    public float agingRate = 0.03f; // Rate at which grass ages, affecting energy and density over time
    public float baseGrowthCost = 0.5f; // Energy cost per metabolic cycle for density growth
    [Tooltip("Clone attempts each plant may make per sim day. 0 = unlimited (one attempt per plant update, the original behaviour).")]
    [Min(0)] public int maxCloneAttemptsPerDay = 0;

    [Header("Breeding and Genera")]
    [Tooltip("Genetic distance at which two plants can no longer breed; below it, pollination odds fall in proportion to distance. " +
             "Unrelated wild plants average about 0.55. Lower = more genera, sooner.")]
    [Min(0.01f)] public float maxBreedingDistance = 0.35f;
    [Tooltip("Real (unscaled) seconds between full genus groupings of living plants. Seeds are checked only when dispersed and when they sprout.")]
    [Min(1f)] public float genusGroupingIntervalSeconds = 60f;

    [Header("Quick Start")]
    [Tooltip("Share of map tiles SpawnRandomGrasses plants, e.g. 0.02 = 2% of tiles (18 plants on a 30x30 map).")]
    [Range(0f, 1f)] public float quickStartGrassFraction = 0.02f;

    [Header("Plant Nutrients")]
    [Tooltip("Most nitrogen or phosphorus a full-size plant can take up per sim second from a rich tile.")]
    [Min(0f)] public float nutrientUptakeRate = 0.004f;
    [Tooltip("Tile nutrient level at which uptake runs at half speed.")]
    [Min(0.01f)] public float nutrientHalfSaturation = 5f;
    [Tooltip("Energy spent per unit of nutrient taken up.")]
    [Min(0f)] public float uptakeEnergyCost = 5f;
    [Tooltip("Leaf nitrogen a full-size plant wants; smaller plants want proportionally less. Hunger, and so uptake, falls to nothing at this level.")]
    [Min(0.01f)] public float leafNitrogenTarget = 6f;
    [Tooltip("Share of leaf nitrogen lost to turnover each sim day. Keeps plants drawing from the tile for life.")]
    [Range(0f, 1f)] public float nitrogenTurnoverPerDay = 0.08f;
    [Tooltip("Share of turnover loss returned to the tile; the rest is gone.")]
    [Range(0f, 1f)] public float nitrogenTurnoverReturnShare = 0.5f;
    [Tooltip("Photosynthesis multiplier with no leaf nitrogen.")]
    [Min(0f)] public float nitrogenStarvedOutput = 0.3f;
    [Tooltip("How far photosynthesis rises above the starved level as leaf nitrogen climbs (saturating). With the defaults, a plant on target runs at x1.")]
    [Min(0f)] public float nitrogenOutputGain = 1.05f;
    [Tooltip("Leaf nitrogen, as a share of target, at which half of that rise is reached.")]
    [Min(0.01f)] public float nitrogenOutputHalfRatio = 0.5f;
    [Tooltip("Lowest leaf nitrogen, as a share of target, at which a plant can clone, on top of the energy cloning needs.")]
    [Min(0f)] public float cloneMinNitrogenRatio = 0.4f;
    [Tooltip("Share of its leaf nitrogen a parent gives each clone. The clone starts with it, and the parent is diluted by it.")]
    [Range(0f, 1f)] public float cloneNitrogenShare = 0.1f;
    [Tooltip("Flowerings' worth of phosphorus a full-size plant that can flower holds at most.")]
    [Min(0f)] public float phosphorusFloweringsHeld = 2f;
    [Tooltip("Sim days a plant must wait to flower again after it finishes flowering or seeding.")]
    [Min(0f)] public float floweringCooldownDays = 2f;
    [Tooltip("Phosphorus spent each time a plant flowers, on top of the energy flowering needs.")]
    [Min(0f)] public float flowerPhosphorusCost = 1f;
    [Tooltip("Share of a dead plant's nitrogen and phosphorus returned to its tile; the rest is lost.")]
    [Range(0f, 1f)] public float nutrientReturnShare = 0.6f;

    [Header("Height")]
    [Tooltip("Energy cost of growth at height 0.1, relative to default height 0.5.")]
    [Min(0f)] public float shortestGrowthEnergy = 0.8f;
    [Tooltip("Energy cost of growth at height 1, relative to default height 0.5.")]
    [Min(0f)] public float tallestGrowthEnergy = 1.5f;
    [Tooltip("Share of light lost when all 8 neighbours' canopies stand one height unit above the plant. Each taller neighbour adds its share by how far it stands above.")]
    [Range(0f, 1f)] public float heightShadeStrength = 0.8f;
    [Tooltip("How many times per sim day plants re-check their neighbours for shade.")]
    [Min(1)] public int shadeUpdatesPerDay = 6;

    [Header("Roots and Nutrient Focus")]
    [Tooltip("Root tiles a day a plant short of nutrients can grow (a well-fed one grows at 30% of this). Roots grow one tile at a time, orthogonally, within the RootReach gene's distance.")]
    [Min(0f)] public float rootGrowthPerDay = 2f;
    [Tooltip("Energy to grow one root tile, for a full-size plant (smaller plants pay less).")]
    [Min(0f)] public float rootBuildEnergyPerTile = 15f;
    [Tooltip("Energy per sim second to keep one root tile alive, for a full-size plant (smaller plants pay less).")]
    [Min(0f)] public float rootUpkeepPerTile = 0.004f;
    [Tooltip("Root tiles lost per sim day while a plant is wilting from lack of energy, outermost first.")]
    [Min(0f)] public float rootDiebackPerDay = 2f;
    [Tooltip("Uptake from each root tile, relative to the plant's own tile, for a plant whose water needs suit that tile.")]
    [Min(0f)] public float rootTileUptake = 0.25f;
    [Tooltip("Roots grow while the plant's nutrient hunger (0 = on target, 1 = none) is above this. Lower means larger root systems.")]
    [Range(0f, 1f)] public float rootHungerSetPoint = 0.15f;
    [Tooltip("A new root only grows into a tile at least this good (0-1): how readily it gives what the plant lacks, times the plant's water fit there.")]
    [Range(0f, 1f)] public float rootMinTileQuality = 0.25f;
    [Tooltip("A root in a tile that falls below this quality is shed, one a day.")]
    [Range(0f, 1f)] public float rootShedQuality = 0.12f;
    [Tooltip("A plant whose nutrient hunger is below this sheds its least useful root, one a day.")]
    [Range(0f, 1f)] public float rootShedHunger = 0.04f;
    [Tooltip("Share of phosphorus roots draw from root tiles, relative to nitrogen. Phosphate barely moves in soil.")]
    [Range(0f, 1f)] public float phosphorusRootAccess = 0.5f;
    [Tooltip("Half-saturation factor at full nutrient focus (-1 or +1): the favoured nutrient's is divided by this, the other's multiplied.")]
    [Min(1f)] public float nutrientFocusStrength = 2f;

    // Collection of active grass instances for individual updates
    internal Dictionary<Vector3Int, GrassInstance> activeGrassInstances = new Dictionary<Vector3Int, GrassInstance>();
    
    // [OPTIMIZED] Position list pool - reduces allocation pressure during update loops
    private const int LIST_POS_POOL_SIZE = 8; // [OPTIMIZED] Increased pool size for high-grass-density scenarios
    private ListPool<Vector3Int> positionListPool;
    private const long MAX_PLANT_COUNTER = 1_000_000_000L;
    internal long plantCount = 0;

    internal readonly GenusTracker genusTracker = new GenusTracker();
    private readonly List<GrassInstance> plantsToUpdate = new List<GrassInstance>();
    private readonly List<PlantGenome> plantGenomesScratch = new List<PlantGenome>();
    private float genusGroupingTimer = 0f;
    private readonly List<GrassInstance> nutrientOrder = new List<GrassInstance>();
    private const int NEIGHBORS_PER_TILE = 8;
    private float shadeTimer = 0f;

    void Start()
    {
        // [OPTIMIZED] Initialize pool with larger capacity for typical simulation
        positionListPool = new ListPool<Vector3Int>(LIST_POS_POOL_SIZE, 16);
    }

    internal GrassInstance.PlantGrowthContext BuildGrowthContext()
    {
        return new GrassInstance.PlantGrowthContext
        {
            shortestGrowthEnergy = shortestGrowthEnergy,
            tallestGrowthEnergy = tallestGrowthEnergy,
            dayLength = TimeController.fullDayLength,
            rootGrowthPerDay = rootGrowthPerDay,
            rootBuildEnergyPerTile = rootBuildEnergyPerTile,
            rootUpkeepPerTile = rootUpkeepPerTile,
            rootDiebackPerDay = rootDiebackPerDay,
            rootHungerSetPoint = rootHungerSetPoint,
            rootMinTileQuality = rootMinTileQuality,
            rootShedQuality = rootShedQuality,
            rootShedHunger = rootShedHunger,
            halfSaturation = nutrientHalfSaturation,
            focusStrength = nutrientFocusStrength,
            phosphorusRootAccess = phosphorusRootAccess
        };
    }

    internal void UpdateAllGrassInstances(float simTime)
    {
        GrassInstance.PlantGrowthContext growthContext = BuildGrowthContext();

        shadeTimer += simTime;
        if (shadeTimer >= TimeController.fullDayLength / Mathf.Max(1, shadeUpdatesPerDay))
        {
            shadeTimer = 0f;
            UpdateShade();
        }

        // Copied because plants are added and removed during the pass
        plantsToUpdate.Clear();
        plantsToUpdate.AddRange(activeGrassInstances.Values);

        for (int i = 0; i < plantsToUpdate.Count; i++)
        {
            GrassInstance grassInstance = plantsToUpdate[i];
            Vector3Int tilePos = grassInstance.tilePosition;

            // Get the corresponding environment data
            EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);

            // Check if the grass still exists (may have been removed)
            if (data.grass != grassInstance)
                continue;

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
                grassInstance.DailyRootShedding(data, in growthContext);
            }

            grassInstance.PerformMetabolism(data, metabolismCostPerAllele, simTime, baseGrowthCost, TimeController.isNight, growthContext);

            // Check if grass should reproduce (handles energy deduction internally)
            if (!grassInstance.IsDormant && grassInstance.rootCount > 0 && grassInstance.LeafNitrogenRatio(leafNitrogenTarget) >= cloneMinNitrogenRatio && grassInstance.CheckAndHandleSpread() && TryUseCloneAttempt(grassInstance))
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
            // The cooldown holds at full while the plant flowers or seeds, however that ends, then runs down
            if (grassInstance.IsFlowering || grassInstance.IsSeeding)
            {
                grassInstance.flowerCooldownLeft = floweringCooldownDays * TimeController.fullDayLength;
            }
            else if (grassInstance.flowerCooldownLeft > 0f)
            {
                grassInstance.flowerCooldownLeft -= simTime;
            }

            bool startedFloweringThisTick = false;
            // Check if grass should flower
            if (!grassInstance.IsDormant && !grassInstance.IsFlowering && !grassInstance.IsSeeding && grassInstance.flowerCooldownLeft <= 0f
                && grassInstance.phosphorusReserve >= flowerPhosphorusCost && grassInstance.CheckFlowering())
            {
                startedFloweringThisTick = true;
                Flower(grassInstance);
                SpreadPollen(grassInstance, tilePos);
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

        if (TimeController.accumulatedAge > 0)
        {
            TimeController.accumulatedAge -= 1; // Decrement accumulated age after applying aging effects to grass instances
        }
    }

    /// <summary>
    /// Each plant's nitrogen turnover and feeding through its roots. Runs with the tile nutrient pass, over the time since
    /// the last one. Plants rooted in the same tiles draw on the same pools, so the pass starts at a random plant each
    /// time rather than always favouring the same ones.
    /// </summary>
    internal void UpdatePlantNutrients(float simTime)
    {
        GrassInstance.NutrientContext context = new GrassInstance.NutrientContext
        {
            maxUptake = nutrientUptakeRate * simTime,
            tileHalfSaturation = nutrientHalfSaturation,
            uptakeEnergyCost = uptakeEnergyCost,
            leafNitrogenTarget = leafNitrogenTarget,
            phosphorusTarget = flowerPhosphorusCost * phosphorusFloweringsHeld,
            turnover = Mathf.Clamp01(nitrogenTurnoverPerDay * simTime / Mathf.Max(0.0001f, TimeController.fullDayLength)),
            turnoverReturnShare = nitrogenTurnoverReturnShare,
            starvedOutput = nitrogenStarvedOutput,
            outputGain = nitrogenOutputGain,
            outputHalfRatio = nitrogenOutputHalfRatio,
            focusStrength = nutrientFocusStrength,
            rootTileUptake = rootTileUptake,
            phosphorusRootAccess = phosphorusRootAccess
        };

        nutrientOrder.Clear();
        nutrientOrder.AddRange(activeGrassInstances.Values);
        int count = nutrientOrder.Count;
        int start = count > 0 ? Random.Range(0, count) : 0;
        for (int k = 0; k < count; k++)
        {
            GrassInstance grass = nutrientOrder[(start + k) % count];
            grass.UpdateNutrients(mapManager.GetTileEnvironmentData(grass.tilePosition), in context);
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

        // Clones start only on one of the parent's root tiles, as rhizomes and runners do
        int rootSlot = originData.grass.RandomRootSlot();
        if (rootSlot < 0)
            return;
        Vector3Int spreadPos = origin + RootZone.Offsets[rootSlot];
        if (!mapManager.IsValidTilePosition(spreadPos))
            return;

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
            float nitrogenGift = originData.grass.leafNitrogen * cloneNitrogenShare;
            originData.grass.leafNitrogen -= nitrogenGift;
            targetData.grass.leafNitrogen = nitrogenGift;

            // Register the grass instance for updates
            activeGrassInstances[gridposition] = targetData.grass;
            plantCount = IncrementWithWrap(plantCount);
            ResearchCounters.RecordBirth(BirthCause.Clone);

            mapManager.UpdateGrassTile(targetData, gridposition);
        }
    }

    /// <summary>
    /// Part of a dead plant's nitrogen and phosphorus returns to its tile.
    /// </summary>
    private void ReturnPlantToTile(GrassInstance grass, EnvironmentData tile)
    {
        if (grass == null || environmentManager == null)
            return;
        tile.availableNitrogen += grass.leafNitrogen * nutrientReturnShare;
        tile.availablePhosphorus += (grass.phosphorusReserve + grass.seedPhosphorus) * nutrientReturnShare;
    }

    internal void SpawnNewGrass(EnvironmentData targetData, Vector3Int gridposition, PlantGenome genome = null, float iniEnergy = 0f, BirthCause cause = BirthCause.Player)
    {
        ResearchCounters.RecordBirth(cause);
        bool hadGrass = targetData.grass != null;
        ReturnPlantToTile(targetData.grass, targetData); // Should not happen (callers spawn on empty tiles)
        PlantGenome newGenome = genome ?? geneManager.GenerateNewGenome(targetData, true);
        if (cause == BirthCause.Seedling && newGenome.genusId != PlantGenome.NO_GENUS)
        {
            genusTracker.PlaceSprout(newGenome, maxBreedingDistance, TimeController.simDaysSinceStart); // A seed's second genus check
        }
        else
        {
            EnsureGenusAssigned(newGenome);
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
    /// New plants have no mother to inherit a genus from. Library species keep their saved names and their own
    /// genus; wild plants are placed by genetics.
    /// </summary>
    private void EnsureGenusAssigned(PlantGenome genome)
    {
        if (genome.genusId != PlantGenome.NO_GENUS)
            return;

        if (string.IsNullOrEmpty(genome.speciesGuid))
        {
            genusTracker.Assign(genome, maxBreedingDistance, TimeController.simDaysSinceStart);
        }
        else
        {
            genusTracker.AssignLibrary(genome, genome.genus, genome.species, genome.speciesGuid, TimeController.simDaysSinceStart);
        }
    }

    /// <summary>
    /// Counts real (unscaled) time while the simulation runs and regroups living plants into genera every
    /// genusGroupingIntervalSeconds. Called by TimeController each unpaused frame.
    /// </summary>
    internal void TickGenusGrouping(float unscaledDeltaTime)
    {
        genusGroupingTimer += unscaledDeltaTime;
        if (genusGroupingTimer < Mathf.Max(1f, genusGroupingIntervalSeconds))
            return;
        genusGroupingTimer = 0f;
        GroupPlantGenera();
    }

    /// <summary>
    /// Full genus grouping of all living plants (genetic comparisons). Seeds are not scanned; see GenusTracker.
    /// </summary>
    internal void GroupPlantGenera(bool removeExtinct = true)
    {
        CollectPlantGenomes();
        genusTracker.GroupPlants(plantGenomesScratch, maxBreedingDistance, TimeController.simDaysSinceStart, removeExtinct);
    }

    /// <summary>
    /// Daily (dawn) plant recount per genus, without genetic comparisons, so the research log stays current.
    /// </summary>
    internal void RecountGenera()
    {
        CollectPlantGenomes();
        genusTracker.RecountPlants(plantGenomesScratch);
    }

    private void CollectPlantGenomes()
    {
        plantGenomesScratch.Clear();
        foreach (GrassInstance grass in activeGrassInstances.Values)
        {
            plantGenomesScratch.Add(grass.genome);
        }
    }

    /// <summary>
    /// After a load: groups the plants, then counts the loaded seeds into their genera (checking only seeds whose genus
    /// is missing). Nothing is removed until the seeds are counted, so genera alive only as seeds survive.
    /// </summary>
    internal void RebuildGeneraAfterLoad()
    {
        genusTracker.ClearSeedCounts();
        GroupPlantGenera(false);
        if (seedManager != null)
        {
            foreach (List<Seed> seeds in seedManager.activeSeeds.Values)
            {
                for (int i = 0; i < seeds.Count; i++)
                {
                    Seed seed = seeds[i];
                    if (!genusTracker.TryGetGenus(seed.carriedGenome.genusId, out GenusRecord genus))
                    {
                        genus = genusTracker.ClassifySeed(seed.carriedGenome, null, maxBreedingDistance, TimeController.simDaysSinceStart);
                    }
                    genusTracker.AddSeed(genus);
                    seed.genusId = genus.id;
                    seeds[i] = seed;
                }
            }
        }
        genusTracker.RemoveExtinct();
        genusGroupingTimer = 0f;
    }

    /// <summary>
    /// Shade update: each plant is dimmed by every neighbouring canopy that stands above its own height, in proportion to
    /// how far it stands above. A plant's own height is genetic, so newborns aren't shaded by adults of the same height,
    /// and canopy grows with size, so newborns don't shade anyone.
    /// </summary>
    internal void UpdateShade()
    {
        foreach (KeyValuePair<Vector3Int, GrassInstance> entry in activeGrassInstances)
        {
            float ownHeight = entry.Value.Height;
            float canopyAbove = 0f;
            foreach (Vector3Int neighborPos in mapManager.GetTileEnvironmentData(entry.Key).immediateNeighbors)
            {
                GrassInstance neighbor = mapManager.GetTileEnvironmentData(neighborPos).grass;
                if (neighbor != null)
                {
                    canopyAbove += Mathf.Max(0f, neighbor.CanopyHeight - ownHeight);
                }
            }
            entry.Value.shadeMultiplier = 1f - heightShadeStrength * canopyAbove / NEIGHBORS_PER_TILE;
        }
    }

    /// <summary>
    /// Pollination odds multiplier: 1 for identical plants, falling to 0 at maxBreedingDistance.
    /// Plants in different genera can never breed.
    /// </summary>
    private float BreedingCompatibility(PlantGenome a, PlantGenome b)
    {
        if (a.genusId != b.genusId)
            return 0f;
        return Mathf.Clamp01(1f - GeneManager.GeneticDistance(a, b) / maxBreedingDistance);
    }

    /// <summary>
    /// Quick start: plants random wild genomes on random free tiles (land or water), with the count proportional
    /// to map size (quickStartGrassFraction). Each plant adapts to its tile, as with a manual spawn.
    /// Public for wiring to a UI button. Returns the number of plants added.
    /// </summary>
    [ContextMenu("Spawn Random Grasses")]
    public int SpawnRandomGrasses()
    {
        if (mapManager == null)
        {
            Debug.LogWarning("SpawnRandomGrasses failed: MapManager reference is missing.");
            return 0;
        }

        int spawned = 0;
        using (var pooled = GetPooledPositions())
        {
            // Work on a copy: GetAllTilePositions returns the map's shared list
            List<Vector3Int> tiles = pooled.List;
            tiles.AddRange(mapManager.GetAllTilePositions());
            int target = Mathf.RoundToInt(tiles.Count * Mathf.Clamp01(quickStartGrassFraction));

            // Partial Fisher-Yates: visits tiles in random order without repeats, skipping occupied ones
            for (int i = 0; i < tiles.Count && spawned < target; i++)
            {
                int pick = Random.Range(i, tiles.Count);
                Vector3Int tile = tiles[pick];
                tiles[pick] = tiles[i];

                EnvironmentData data = mapManager.GetTileEnvironmentData(tile);
                if (data.grass != null)
                    continue;

                SpawnNewGrass(data, tile);
                spawned++;
            }
        }
        return spawned;
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
    /// Members of the species' genus take the new names (descendants that split into another genus keep theirs);
    /// its pure lineage also carries the old epithet as origin tag.
    /// Hybrid origin tags ("A × B") are historical records and are left untouched.
    /// </summary>
    internal void ApplySpeciesRename(string speciesGuid, string oldSpecies, string newGenus, string newSpecies)
    {
        if (string.IsNullOrEmpty(speciesGuid))
            return;

        GenusRecord genus = genusTracker.RenameLibrarySpecies(speciesGuid, newGenus, newSpecies, TimeController.simDaysSinceStart, out int oldGenusId);

        foreach (GrassInstance grass in activeGrassInstances.Values)
        {
            RenameGenome(grass.genome, speciesGuid, oldSpecies, oldGenusId, genus, newSpecies);
            RenameGenome(grass.seedGenome, speciesGuid, oldSpecies, oldGenusId, genus, newSpecies);
        }

        if (seedManager != null)
        {
            foreach (List<Seed> seeds in seedManager.activeSeeds.Values)
            {
                for (int i = 0; i < seeds.Count; i++)
                {
                    Seed seed = seeds[i];
                    RenameGenome(seed.carriedGenome, speciesGuid, oldSpecies, oldGenusId, genus, newSpecies);
                    if (genus != null && genus.id != oldGenusId && seed.genusId == oldGenusId && seed.carriedGenome.genusId == genus.id)
                    {
                        genusTracker.MoveSeed(oldGenusId, genus);
                        seed.genusId = genus.id;
                        seeds[i] = seed;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Makes a living plant the founding specimen of a newly saved library species, using the same convention as
    /// plants spawned from the library (its own genus under the saved names, origin tag = epithet). The plant gets its
    /// own genome copy so genetically identical siblings sharing the genome object are not swept into the species.
    /// </summary>
    internal void AssignSpecimenToSpecies(GrassInstance grass, string speciesGuid, string genus, string species)
    {
        if (grass == null || grass.genome == null || string.IsNullOrEmpty(speciesGuid))
            return;

        PlantGenome specimen = grass.genome.DeepCopy();
        specimen.speciesGuid = speciesGuid;
        specimen.origin = species;
        genusTracker.AssignLibrary(specimen, genus, species, speciesGuid, TimeController.simDaysSinceStart);
        grass.genome = specimen;
    }

    private static void RenameGenome(PlantGenome genome, string speciesGuid, string oldSpecies, int oldGenusId, GenusRecord genus, string newSpecies)
    {
        if (genome == null || genome.speciesGuid != speciesGuid)
            return;

        if (genus != null && genome.genusId == oldGenusId)
        {
            genome.genusId = genus.id;
            genome.genusColor = genus.founderColor;
            genome.genus = genus.name;
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
            ReturnPlantToTile(data.grass, data);
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
                // Grass died (starvation, dormancy timeout, withering, or failing to establish); waiting seedlings get first claim
                RemoveGrassInstance(tilePos, data, data.grass.GetDeathCause());
                if (seedManager != null)
                {
                    seedManager.OnTileFreed(tilePos, data);
                }
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

    private void Flower(GrassInstance grass)
    {
        // The phosphorus goes into the coming seeds, not away; it stays with the plant if no seeds come of this flowering
        grass.phosphorusReserve -= flowerPhosphorusCost;
        grass.seedPhosphorus += flowerPhosphorusCost;
        grass.reproductiveState = ReproductiveState.Flowering;
        ResearchCounters.Today.floweringStarts++;
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

                float compatibilityFactor = BreedingCompatibility(targetGrass.genome, grass.genome);
                if (compatibilityFactor <= 0f)
                {
                    if (targetGrass.genome.genusId != grass.genome.genusId)
                    {
                        ResearchCounters.Today.crossesBlockedByGenus++;
                    }
                    continue;
                }

                // Manhattan distance success falloff
                int dist = Mathf.Abs(tile.x - grassPos.x) + Mathf.Abs(tile.y - grassPos.y);
                float maxSuccess = 0.95f;
                float success = range > 0 ? maxSuccess * Mathf.Clamp01(1f - (float)dist / range) : maxSuccess;

                if (Random.value <= success)
                {
                    EnvironmentData receiverData = mapManager.GetTileEnvironmentData(tile);
                    float localRadiation = receiverData.radiation;

                    var maternalGamete = geneManager.Meiosis(targetGrass.genome, localRadiation);
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
                        seedGenome.InheritGenus(targetGrass.genome);

                        targetGrass.reproductiveState = ReproductiveState.Seeding;
                        targetGrass.seedGenome = seedGenome;
                        ResearchCounters.Today.crossPollinations++;
                    }
                }
            }
        }
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
            seedGenome.InheritGenus(parent.genome);

            parent.reproductiveState = ReproductiveState.Seeding;
            parent.seedGenome = seedGenome;
            ResearchCounters.Today.selfPollinations++;
        }
    }

    /// <summary>
    /// Disperse seeds from a seeding grass. Releases SeedCount seeds that split the stored seeding energy evenly;
    /// each lands on a random tile within the Manhattan dispersal range (several may share a tile).
    /// Seeds landing off-map or on a full tile are lost along with their energy; their phosphorus drops under the parent.
    /// </summary>
    private void DisperseSeed(GrassInstance parent, Vector3Int parentPos)
    {
        if (parent == null || parent.genome == null) return;

        parent.reproductiveState = ReproductiveState.None;

        int range = Mathf.Max(1, (int)parent.genome.GetTraitValue(TraitId.SeedDispersalRange)); // reuse trait 21 for seed dispersal radius
        int seedCount = Mathf.Max(1, Mathf.RoundToInt(parent.genome.GetTraitValue(TraitId.SeedCount)));
        float energyPerSeed = parent.seedingEnergy / seedCount;
        float phosphorusPerSeed = parent.seedPhosphorus / seedCount;
        EnvironmentData parentTile = mapManager.GetTileEnvironmentData(parentPos);
        // A seed's first genus check; siblings share the genome, so once per dispersal
        GenusRecord seedGenus = genusTracker.ClassifySeed(parent.seedGenome, parent.genome, maxBreedingDistance, TimeController.simDaysSinceStart);
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
                parentTile.availablePhosphorus += phosphorusPerSeed;
                continue;
            }

            bool added = seedManager.TryAddSeed(tile, new Seed
            {
                carriedGenome = parent.seedGenome,
                genusId = seedGenus.id,
                storedEnergy = energyPerSeed,
                phosphorus = phosphorusPerSeed,
                germinationEnergy = germinationEnergy,
                germinationStage = 0,
                sproutStage = sproutStage,
                germinationCue = germinationCue,
                coldPreservation = coldPreservation
            });
            if (added)
            {
                genusTracker.AddSeed(seedGenus);
            }
            else
            {
                ResearchCounters.Today.seedsLost++; // Tile already at the seed cap
                parentTile.availablePhosphorus += phosphorusPerSeed;
            }
        }
        parent.seedingEnergy = 0f;
        parent.seedPhosphorus = 0f;
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

}
