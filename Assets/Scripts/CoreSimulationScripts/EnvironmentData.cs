using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class EnvironmentData
{
    public float temperature;
    public float hydration;
    public float light;
    public float radiation;
    public float oxygen;
    public bool isWater;

    // Biogeochemical pools. Double precision: they receive tiny increments (~1e-5) on values of 10-100,
    // which float would round away. Carbon cycles through plants; N/P/K are not consumed by grass yet.
    public double soilOrganicCarbon;
    public double dissolvedCarbon;
    public double litterCarbon;     // Decay pool: carbon from dead/shrinking plants, slowly released into soilOrganicCarbon
    public double availableNitrogen;
    public double availablePhosphorus;
    public double availablePotassium;

    // Scratch values used to apply diffusion from a stable oxygen snapshot.
    [System.NonSerialized] public float oxygenBeforeDiffusion;
    [System.NonSerialized] public float oxygenDiffusionDelta;
    [System.NonSerialized] public double nitrogenTransportDelta;
    [System.NonSerialized] public double phosphorusTransportDelta;
    [System.NonSerialized] public double potassiumTransportDelta;

    public GrassInstance grass; // Grass on this tile (includes genetics, energy, and instance state)

    // Cache for tilemap rendering optimization - avoids redundant SetTile/SetColor calls
    [System.NonSerialized]
    public UnityEngine.Tilemaps.TileBase cachedTile;
    [System.NonSerialized]
    // [OPTIMIZED] Initialize to invalid value (-1,-1,-1,-1) to force first update, then cache subsequent colors
    public UnityEngine.Color cachedTileColor = new UnityEngine.Color(-1, -1, -1, -1);
    [System.NonSerialized]
    public List<Vector3Int> immediateNeighbors;
    
    // [OPTIMIZED] Cache static temperature offset (latitude + PerlinNoise effects)
    // This never changes per tile, so compute once and reuse every frame
    [System.NonSerialized]
    public bool staticTempOffsetCalculated = false;
    [System.NonSerialized]
    public float cachedStaticTempOffset = 0f;

    // [OPTIMIZED] Visualization color caching - avoid redundant tilemap updates
    [System.NonSerialized]
    public UnityEngine.Color cachedVisualizationColor = new UnityEngine.Color(-1, -1, -1, -1); // Invalid until first update
    [System.NonSerialized]
    public bool visualizationTileFlagsSet = false;  // Track if SetTileFlags has been called
}
