using UnityEngine;
using System.Collections.Generic;
using System.Reflection;

public class EnvironmentManager : MonoBehaviour
{
    [Header("Temperature Settings")]
    public float baseTemperature = 20f;
    public float hydroOffset = -5f;      // Temperature change at 100% hydration (e.g., -5 degrees)
    public float lightOffset = -5f;      // Temperature change at 0% light/complete darkness (e.g., -5 degrees at night)
    internal float seasonalTemperatureOffset;

    public float minTemperature = -80f;
    public float maxTemperature = 60f;
    public float equatorialBoost = 8f;   // Temperature change at the equator, across the middle of the map
    public float polarPenalty = -8f;     // Temperature change at the north and south edges
    [Tooltip("Share of the seasonal temperature change felt at the equator.")]
    [Min(0f)] public float equatorSeasonStrength = 0.4f;
    [Tooltip("Share of the seasonal temperature change felt at the north and south edges.")]
    [Min(0f)] public float poleSeasonStrength = 1.3f;

    [Header("Hydration Settings")]
    public float baseLandHydration = 20f;
    [Tooltip("Extra hydration on land right beside water, on top of baseLandHydration.")]
    [Min(0f)] public float coastalHydrationBonus = 60f;
    [Tooltip("Tiles inland over which coastal hydration fades to baseLandHydration.")]
    [Min(1)] public int coastalHydrationFadeTiles = 5;

    [Header("Other Factors")]
    public float waterLightLevel = 0f;
    public float landLightLevel = 0f;
    public float waterMaxLightLevel = 0.8f;
    public float landMaxLightLevel = 1f;
    public float waterRadiation = 0.1f;
    public float landRadiation = 0.2f;
    public float waterOxygen = 80f;
    public float landOxygen = 50f;
    [Tooltip("Scales the season's daylight hours (capped so night never goes below 0). A day always stays 24 sim hours; this only shifts the daylight/night ratio.")]
    public float dayNightCycleSpeed = 1f;
    public float baseLightLevel = 0f;

    [Header("Map Nutrient Pools (plants use N and P; K not yet)")]
    public float landNitrogen = 35f;
    public float waterNitrogen = 12f;
    public float landPhosphorus = 24f;
    public float waterPhosphorus = 10f;
    public float landPotassium = 40f;
    public float waterPotassium = 16f;
    [Tooltip("How far each tile's initial nutrients stray from the land/water levels above: 0.6 = between x0.4 and x1.6.")]
    [Range(0f, 1f)] public float nutrientVariation = 0.6f;
    [Tooltip("Rough size in tiles of nutrient-rich and nutrient-poor patches. N, P and K are patterned separately, so their ratios differ across the map.")]
    [Min(1f)] public float nutrientPatchSize = 12f;
    [Header("Nutrient Refill and Erosion")]
    [Tooltip("Share of a tile's nitrogen shortfall (below its initial level) refilled per simulated second.")]
    [Min(0f)] public float nitrogenRefillRate = 0.00003f;
    [Tooltip("Share of a tile's phosphorus shortfall (below its initial level) refilled per simulated second.")]
    [Min(0f)] public float phosphorusRefillRate = 0.00001f;
    [Tooltip("Share of a tile's potassium shortfall (below its initial level) refilled per simulated second.")]
    [Min(0f)] public float potassiumRefillRate = 0.00002f;
    [Tooltip("Share of a tile's excess (above its initial level) lost per simulated second, so eroded nutrients don't pile up in coastal water.")]
    [Min(0f)] public float nutrientSettlingRate = 0.00001f;
    [Tooltip("Fraction of available land nutrients eroded into adjacent water per simulated second, at full hydration with all neighbours water.")]
    [Min(0f)] public float nutrientLeachingRate = 0.000005f;

    [Header("References")]
    public MapManager mapManager;
    public GrassManager grassManager;
    public EnvironmentVisualizer environmentVisualizer;

    [Header("Oxygen Diffusion")]
    [Tooltip("Share of an oxygen difference moved to each lower neighbour per simulated second, before the land (0.2) / water (0.1) rate.")]
    [Min(0f)] public float oxygenDiffusionCoefficient = 0.01f;

    private float lastTileLightLevel = -1f;   // What the last map-wide tile pass used
    private float lastTileSeasonOffset = float.NaN;
    private float nutrientTimeAccumulator;
    private const float NUTRIENT_UPDATE_INTERVAL = 10f;
    private float oxygenTimeAccumulator;
    private const float LAND_OXYGEN_DIFFUSION_RATE = 0.2f;
    private const float WATER_OXYGEN_DIFFUSION_RATE = 0.1f;
    // Stability bound for the explicit scheme: per sub-step a tile may pass at most this share of a difference to all neighbours combined
    private const float MAX_OXYGEN_OUTFLOW_PER_STEP = 0.5f;
    private const int MAX_OXYGEN_SUBSTEPS = 64;

    void Awake()
    {
        baseTemperature = 20f;
        if (SimulationStartupSettings.TryGet(out _, out _, out float startupWaterRadiation, out float startupLandRadiation, out _))
        {
            waterRadiation = startupWaterRadiation;
            landRadiation = startupLandRadiation;
        }
    }

    internal void UpdateTileEnvironment(Vector3Int? centerPosition = null, bool fullUpdate = false, bool distributeOxygen = false, float simTime = 0f, bool skipHydrationUpdate = false)
    {
        // Compute light levels for both tile types
        waterLightLevel = Mathf.Min(baseLightLevel, waterMaxLightLevel);
        landLightLevel = Mathf.Min(baseLightLevel, landMaxLightLevel);

        // Tile light and temperature only follow these two between terrain or settings edits (which pass
        // skipHydrationUpdate false or a position), and both hold still through most of the day and night
        bool tilesUnchanged = skipHydrationUpdate && !centerPosition.HasValue
            && baseLightLevel == lastTileLightLevel && seasonalTemperatureOffset == lastTileSeasonOffset;
        if (!centerPosition.HasValue)
        {
            lastTileLightLevel = baseLightLevel;
            lastTileSeasonOffset = seasonalTemperatureOffset;
        }

        if (!tilesUnchanged)
        {
            RefreshTiles(centerPosition, skipHydrationUpdate);
        }

        if (simTime > 0f)
        {
            nutrientTimeAccumulator += simTime;
            if (nutrientTimeAccumulator >= NUTRIENT_UPDATE_INTERVAL)
            {
                float elapsed = nutrientTimeAccumulator;
                nutrientTimeAccumulator = 0f;
                UpdateNutrientPools(elapsed);
            }
        }

        // Diffusion runs less often than this update, so integrate over all the time elapsed since the last pass
        if (simTime > 0f)
            oxygenTimeAccumulator += simTime;
        if (distributeOxygen && oxygenTimeAccumulator > 0f)
        {
            DistributeOxygen(oxygenTimeAccumulator);
            oxygenTimeAccumulator = 0f;
        }
    }

    private void RefreshTiles(Vector3Int? centerPosition, bool skipHydrationUpdate)
    {
        float mapHeight = mapManager.height;

        using (var pooled = grassManager.GetPooledPositions())
        {
            List<Vector3Int> tilesToCheck = pooled.List;
            if (centerPosition.HasValue)
            {
                // A terrain change reaches as far as coastal hydration does
                int radius = coastalHydrationFadeTiles + 1;
                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        Vector3Int tilePos = centerPosition.Value + new Vector3Int(x, y, 0);
                        if (mapManager.IsValidTilePosition(tilePos))
                        {
                            tilesToCheck.Add(tilePos);
                        }
                    }
                }
            }
            else
            {
                // Check all tiles if no center position is provided
                tilesToCheck.AddRange(mapManager.GetAllTilePositions());
            }

            foreach (var tilePos in tilesToCheck)
            {
                EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);

                // Update light based on tile type (water vs land)
                float targetLight = data.isWater ? waterLightLevel : landLightLevel;
                if (!Mathf.Approximately(data.light, targetLight))
                {
                    data.light = Mathf.Clamp01(targetLight);
                }

                // Recalculate hydration and land tile colors (unless explicitly skipped)
                if (!skipHydrationUpdate)
                {
                    RecalculateHydrationAndRadiation(tilePos, data);
                }

                // Recalculate temperature
                RecalculateTemperature(tilePos, data, mapHeight);

            }
        }
    }

    /// <summary>
    /// Sets a tile's pools to its initial nutrient levels. Call after the initial nutrient map covers the tile.
    /// </summary>
    internal void InitializeNutrientPools(EnvironmentData data, Vector3Int tilePos)
    {
        if (data == null)
            return;

        data.availableNitrogen = GetInitialNutrient(tilePos, Nutrient.Nitrogen);
        data.availablePhosphorus = GetInitialNutrient(tilePos, Nutrient.Phosphorus);
        data.availablePotassium = GetInitialNutrient(tilePos, Nutrient.Potassium);
    }

    // ---- Initial nutrient map ----
    // What each tile refills towards. Random patches for a new map; every tile starts as water, as the map does.

    private const int NUTRIENT_COUNT = 3;
    private const float NUTRIENT_NOISE_OFFSET_RANGE = 1000f;
    private const float NUTRIENT_NOISE_STRETCH = 2.5f; // Perlin noise rarely reaches its extremes; this spreads it over the full range

    private double[,,] initialNutrients; // [map x, map y, Nutrient]

    internal void CreateInitialNutrientMap()
    {
        int width = mapManager.width;
        int height = mapManager.height;
        initialNutrients = new double[width, height, NUTRIENT_COUNT];

        for (int nutrient = 0; nutrient < NUTRIENT_COUNT; nutrient++)
        {
            float noiseX = Random.Range(0f, NUTRIENT_NOISE_OFFSET_RANGE);
            float noiseY = Random.Range(0f, NUTRIENT_NOISE_OFFSET_RANGE);
            double waterLevel = TerrainNutrientLevel((Nutrient)nutrient, true);

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float noise = Mathf.PerlinNoise(noiseX + x / nutrientPatchSize, noiseY + y / nutrientPatchSize);
                    float deviation = Mathf.Clamp((noise - 0.5f) * NUTRIENT_NOISE_STRETCH, -1f, 1f);
                    initialNutrients[x, y, nutrient] = waterLevel * (1f + nutrientVariation * deviation);
                }
            }
        }
    }

    private double TerrainNutrientLevel(Nutrient nutrient, bool isWater)
    {
        float level;
        switch (nutrient)
        {
            case Nutrient.Nitrogen:
                level = isWater ? waterNitrogen : landNitrogen;
                break;
            case Nutrient.Phosphorus:
                level = isWater ? waterPhosphorus : landPhosphorus;
                break;
            default:
                level = isWater ? waterPotassium : landPotassium;
                break;
        }
        return System.Math.Max(0.0, level);
    }

    internal double GetInitialNutrient(Vector3Int tilePos, Nutrient nutrient)
    {
        return initialNutrients[tilePos.x + mapManager.width / 2, tilePos.y + mapManager.height / 2, (int)nutrient];
    }

    internal void SetInitialNutrient(Vector3Int tilePos, Nutrient nutrient, double value)
    {
        initialNutrients[tilePos.x + mapManager.width / 2, tilePos.y + mapManager.height / 2, (int)nutrient] = System.Math.Max(0.0, value);
    }

    /// <summary>
    /// Moves a tile's initial nutrients to its new terrain's level, keeping how rich or poor the tile is for that terrain.
    /// </summary>
    internal void ChangeInitialNutrientTerrain(Vector3Int tilePos, bool wasWater, bool isWater)
    {
        if (wasWater == isWater)
            return;

        for (int i = 0; i < NUTRIENT_COUNT; i++)
        {
            Nutrient nutrient = (Nutrient)i;
            double oldLevel = TerrainNutrientLevel(nutrient, wasWater);
            double newLevel = TerrainNutrientLevel(nutrient, isWater);
            SetInitialNutrient(tilePos, nutrient, oldLevel > 0.0 ? GetInitialNutrient(tilePos, nutrient) * newLevel / oldLevel : newLevel);
        }
    }

    /// <summary>
    /// Refills a tile's nutrient towards its initial level, or lets an excess settle back down.
    /// </summary>
    private static double MoveTowardsInitial(double level, double initial, double refillFraction, double settlingFraction)
    {
        return level < initial
            ? level + (initial - level) * refillFraction
            : level - (level - initial) * settlingFraction;
    }

    private void UpdateNutrientPools(float simTime)
    {
        List<Vector3Int> positions = mapManager.GetAllTilePositions();
        double nitrogenRefill = System.Math.Min(1.0, System.Math.Max(0f, nitrogenRefillRate) * simTime);
        double phosphorusRefill = System.Math.Min(1.0, System.Math.Max(0f, phosphorusRefillRate) * simTime);
        double potassiumRefill = System.Math.Min(1.0, System.Math.Max(0f, potassiumRefillRate) * simTime);
        double settling = System.Math.Min(1.0, System.Math.Max(0f, nutrientSettlingRate) * simTime);

        // Nutrient refill per tile.
        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);

            data.availableNitrogen = MoveTowardsInitial(data.availableNitrogen, GetInitialNutrient(pos, Nutrient.Nitrogen), nitrogenRefill, settling);
            data.availablePhosphorus = MoveTowardsInitial(data.availablePhosphorus, GetInitialNutrient(pos, Nutrient.Phosphorus), phosphorusRefill, settling);
            data.availablePotassium = MoveTowardsInitial(data.availablePotassium, GetInitialNutrient(pos, Nutrient.Potassium), potassiumRefill, settling);

            data.nitrogenTransportDelta = 0.0;
            data.phosphorusTransportDelta = 0.0;
            data.potassiumTransportDelta = 0.0;
        }

        grassManager.UpdatePlantNutrients(simTime);

        // Erosion carries a small share of land nutrients into adjacent water.
        foreach (Vector3Int pos in positions)
        {
            EnvironmentData source = mapManager.GetTileEnvironmentData(pos);
            if (source.isWater || source.immediateNeighbors == null || source.immediateNeighbors.Count == 0)
                continue;

            int waterNeighbors = 0;
            foreach (Vector3Int neighborPos in source.immediateNeighbors)
            {
                if (mapManager.GetTileEnvironmentData(neighborPos).isWater)
                    waterNeighbors++;
            }
            if (waterNeighbors == 0)
                continue;

            double runoff = System.Math.Min(1.0, System.Math.Max(0f, nutrientLeachingRate) * simTime
                * Mathf.Clamp01(source.hydration / 100f) * waterNeighbors / source.immediateNeighbors.Count);
            TransferNutrient(source, runoff, waterNeighbors);
        }

        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            data.availableNitrogen = ClampNutrient(data.availableNitrogen + data.nitrogenTransportDelta);
            data.availablePhosphorus = ClampNutrient(data.availablePhosphorus + data.phosphorusTransportDelta);
            data.availablePotassium = ClampNutrient(data.availablePotassium + data.potassiumTransportDelta);
        }
    }

    private static double ClampNutrient(double value)
    {
        return System.Math.Min(System.Math.Max(value, 0.0), 100.0);
    }

    private void TransferNutrient(EnvironmentData source, double fraction, int waterNeighbors)
    {
        double nitrogen = source.availableNitrogen * fraction;
        double phosphorus = source.availablePhosphorus * fraction;
        double potassium = source.availablePotassium * fraction;
        source.availableNitrogen -= nitrogen;
        source.availablePhosphorus -= phosphorus;
        source.availablePotassium -= potassium;

        double divisor = System.Math.Max(1, waterNeighbors);
        foreach (Vector3Int neighborPos in source.immediateNeighbors)
        {
            EnvironmentData neighbor = mapManager.GetTileEnvironmentData(neighborPos);
            if (!neighbor.isWater)
                continue;
            neighbor.nitrogenTransportDelta += nitrogen / divisor;
            neighbor.phosphorusTransportDelta += phosphorus / divisor;
            neighbor.potassiumTransportDelta += potassium / divisor;
        }
    }

    /// <summary>
    /// [OPTIMIZED] Recalculate temperature based on current light, hydration, and cached latitude.
    /// Only recalculates dynamic parts - light and hydration effects.
    /// Must run every frame since light changes continuously with day-night cycle.
    /// </summary>
    private void RecalculateTemperature(Vector3Int tilePos, EnvironmentData data, float mapHeight)
    {
        // [OPTIMIZED] Calculate and cache static temperature offset (latitude + PerlinNoise) only once
        // This is completely static and doesn't need to recalculate every frame
        if (!data.staticTempOffsetCalculated)
        {
            // 0 at the equator, 1 at the north and south edges
            float distanceFromEquator = Mathf.Abs(tilePos.y + mapManager.height / 2 + 0.5f - mapHeight / 2f);
            float latitude = Mathf.Clamp01(distanceFromEquator / (mapHeight / 2f));
            float latitudeEffect = Mathf.PerlinNoise(tilePos.x * 0.1f, tilePos.y * 0.1f) * 2 - 1;
            data.cachedStaticTempOffset = Mathf.Lerp(equatorialBoost, polarPenalty, latitude) + latitudeEffect;
            data.cachedSeasonStrength = Mathf.Lerp(equatorSeasonStrength, poleSeasonStrength, latitude);
            data.staticTempOffsetCalculated = true;
        }
        
        // Base temperature from seasonal and static offsets
        float temperature = baseTemperature + data.cachedStaticTempOffset + seasonalTemperatureOffset * data.cachedSeasonStrength;

        // Apply temperature offsets proportionally based on hydration and light
        // Hydration effect: 100% hydration = 100% of hydroOffset applied
        float hydrationEffect = (data.hydration / 100f) * hydroOffset;
        // Light effect: 0% light (night) = 100% of lightOffset applied
        // data.light is always current since we update it first every frame
        float lightEffect = (1f - data.light) * lightOffset;
        data.temperature = temperature + hydrationEffect + lightEffect;

        // Clamp temperature to valid range
        data.temperature = Mathf.Clamp(data.temperature, minTemperature, maxTemperature);
    }

    /// <summary>
    /// [UPDATED] Recalculate hydration, radiation, and visual feedback.
    /// Called only when environment topology changes (not every frame).
    /// </summary>
    private void RecalculateHydrationAndRadiation(Vector3Int tilePos, EnvironmentData data)
    {
        if (data.isWater)
        {
            data.hydration = 100f; // Water is always fully hydrated
            data.radiation = waterRadiation;
        }
        else
        {
            // Hydration calculation for land tiles
            float hydration = CalculateLandHydration(tilePos);
            data.hydration = hydration;
            
            // Apply visual feedback based on hydration - brightness indicates dryness/wetness
            float t = Mathf.Clamp01(hydration / 100f);
            float brightness = Mathf.Lerp(1f, 0.4f, t);
            Color tileColor = new Color(brightness, brightness, brightness);
            
            // Only call SetColor if the color actually changed (avoid redundant tilemap updates)
            if (data.cachedTileColor != tileColor)
            {
                mapManager.terrainTilemap.SetColor(tilePos, tileColor);
                data.cachedTileColor = tileColor;
            }
            
            data.radiation = landRadiation;
        }
        
        // Clamp values to valid ranges
        data.hydration = Mathf.Clamp(data.hydration, 0f, 100f);
        data.radiation = Mathf.Clamp01(data.radiation);
    }

    public float CalculateLandHydration(Vector3Int tilePos)
    {
        int searchRadius = coastalHydrationFadeTiles + 1;
        float closestWaterDistance = float.MaxValue;

        // Find closest water tile within search radius
        for (int x = -searchRadius; x <= searchRadius; x++)
        {
            for (int y = -searchRadius; y <= searchRadius; y++)
            {
                if (x == 0 && y == 0) continue;

                int squaredDistance = x * x + y * y;
                // Only consider tiles within circular radius
                if (squaredDistance > searchRadius * searchRadius)
                    continue;

                Vector3Int checkPos = tilePos + new Vector3Int(x, y, 0);
                if (!mapManager.IsValidTilePosition(checkPos))
                    continue;

                EnvironmentData neighborData = mapManager.GetTileEnvironmentData(checkPos);
                if (neighborData.isWater)
                {
                    float distance = Mathf.Sqrt(squaredDistance);
                    if (distance < closestWaterDistance)
                    {
                        closestWaterDistance = distance;
                    }
                }
            }
        }

        float result;
        if (closestWaterDistance == float.MaxValue)
        {
            // No water found within radius - use base land hydration
            result = baseLandHydration;
        }
        else if (closestWaterDistance <= 1f)
        {
            // Adjacent to water - use max coastal hydration
            result = Mathf.Min(baseLandHydration + coastalHydrationBonus, 100f);
        }
        else
        {
            // Linear interpolation based on distance, from coastal hydration at distance 1 to
            // baseLandHydration at the end of the fade
            float t = Mathf.Clamp01((closestWaterDistance - 1f) / coastalHydrationFadeTiles);
            float coastalHydration = Mathf.Min(baseLandHydration + coastalHydrationBonus, 100f);
            result = Mathf.Lerp(coastalHydration, baseLandHydration, t);
        }

        return result;
    }

    /// <summary>
    /// Diffuses oxygen between same-type neighbours over the elapsed time, split into sub-steps small enough
    /// that no tile can overshoot its neighbours (the explicit scheme otherwise oscillates for large steps).
    /// </summary>
    private void DistributeOxygen(float elapsed)
    {
        float maxOutflowPerSecond = LAND_OXYGEN_DIFFUSION_RATE * Mathf.Max(0f, oxygenDiffusionCoefficient) * 8f; // 8 neighbours
        int subSteps = Mathf.Clamp(Mathf.CeilToInt(maxOutflowPerSecond * elapsed / MAX_OXYGEN_OUTFLOW_PER_STEP), 1, MAX_OXYGEN_SUBSTEPS);
        float stepTime = elapsed / subSteps;
        for (int i = 0; i < subSteps; i++)
        {
            DiffuseOxygenStep(stepTime);
        }
    }

    private void DiffuseOxygenStep(float simTime)
    {
        List<Vector3Int> positions = mapManager.GetAllTilePositions();
        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            data.oxygenBeforeDiffusion = Mathf.Max(0f, data.oxygen);
            data.oxygenDiffusionDelta = 0f;
        }

        foreach (Vector3Int pos in positions)
        {
            EnvironmentData source = mapManager.GetTileEnvironmentData(pos);
            if (source.immediateNeighbors == null || source.immediateNeighbors.Count == 0)
                continue;

            float coefficient = (source.isWater ? WATER_OXYGEN_DIFFUSION_RATE : LAND_OXYGEN_DIFFUSION_RATE)
                * Mathf.Max(0f, oxygenDiffusionCoefficient) * simTime;
            float totalRequested = 0f;
            foreach (Vector3Int neighborPos in source.immediateNeighbors)
            {
                EnvironmentData neighbor = mapManager.GetTileEnvironmentData(neighborPos);
                if (source.isWater == neighbor.isWater && neighbor.oxygenBeforeDiffusion < source.oxygenBeforeDiffusion)
                    totalRequested += (source.oxygenBeforeDiffusion - neighbor.oxygenBeforeDiffusion) * coefficient;
            }

            float scale = totalRequested > source.oxygenBeforeDiffusion && totalRequested > 0f
                ? source.oxygenBeforeDiffusion / totalRequested
                : 1f;
            foreach (Vector3Int neighborPos in source.immediateNeighbors)
            {
                EnvironmentData neighbor = mapManager.GetTileEnvironmentData(neighborPos);
                if (source.isWater != neighbor.isWater || neighbor.oxygenBeforeDiffusion >= source.oxygenBeforeDiffusion)
                    continue;

                float transfer = (source.oxygenBeforeDiffusion - neighbor.oxygenBeforeDiffusion) * coefficient * scale;
                source.oxygenDiffusionDelta -= transfer;
                neighbor.oxygenDiffusionDelta += transfer;
            }
        }

        foreach (Vector3Int pos in positions)
        {
            EnvironmentData data = mapManager.GetTileEnvironmentData(pos);
            data.oxygen = Mathf.Clamp(data.oxygenBeforeDiffusion + data.oxygenDiffusionDelta, 0f, 100f);
        }
    }

    internal void VisualUpdate()
    {
        if (environmentVisualizer != null && !string.IsNullOrEmpty(environmentVisualizer.activeVisualization))
        {
            foreach (var tilePos in mapManager.GetAllTilePositions())
            {
                EnvironmentData data = mapManager.GetTileEnvironmentData(tilePos);
            
                // [OPTIMIZED] Calculate visualization color from current data
                Color tileColor = environmentVisualizer.VisualizeFactor(data);
                
                // [OPTIMIZED] Only update overlay if color has changed (avoid redundant SetColor calls)
                // This is critical because VisualUpdate is called 50x per second
                if (data.cachedVisualizationColor != tileColor)
                {
                    mapManager.SetOverlayColor(tilePos, tileColor, data);
                    data.cachedVisualizationColor = tileColor;
                }
            }
        }
    }

    public void ParameterSlider(string parameterName, float newValue)
    {
        // Example of using reflection to dynamically adjust parameters via UI sliders
        // This allows for easy tweaking without hardcoding each parameter
        // Sliders should be set up to call this method with the parameter name and new value

        FieldInfo field = typeof(EnvironmentManager).GetField(
            parameterName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
        ); // Only allow public instance fields to be modified for safety
        
        if (field != null && field.FieldType == typeof(float))
        {
            field.SetValue(this, newValue);
            // The periodic update skips hydration/radiation, so push setting changes to every tile now.
            if (mapManager != null && mapManager.GetAllTilePositions().Count > 0)
            {
                UpdateTileEnvironment(null, true);
            }
        }
        else
        {
            Debug.LogWarning($"Parameter '{parameterName}' not found or is not a float.");
        }
    }

    internal float GetParameterValue(string parameterName)
    {
        FieldInfo field = typeof(EnvironmentManager).GetField(
            parameterName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
        );
        
        if (field != null && field.FieldType == typeof(float))
        {
            return (float)field.GetValue(this);
        }
        else
        {
            Debug.LogWarning($"Parameter '{parameterName}' not found or is not a float.");
            return 0f;
        }
    }
}
