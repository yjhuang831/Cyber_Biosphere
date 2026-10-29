using UnityEngine;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using UnityEngine.EventSystems;

public class MapManager : MonoBehaviour
{
    [Header("Tile References")]
    public Tilemap terrainTilemap;
    public Tilemap overlayTilemap;
    public Tilemap grassTilemap;
    public TileBase landTile;
    public TileBase[] landTiles = new TileBase[0];
    public TileBase waterTile;
    public TileBase grassTile;
    public TileBase transparentTile;
    private bool changedTileIsLand = false;

    [Header("Grass Growth Stage Tiles")]
    public TileBase[] grassDryTiles = new TileBase[6];          // young, medium, mature, young-dormant, medium-dormant, mature-dormant
    public TileBase[] grassBalancedTiles = new TileBase[6];     // young, medium, mature, young-dormant, medium-dormant, mature-dormant
    public TileBase[] grassWetTiles = new TileBase[6];          // young, medium, mature, young-dormant, medium-dormant, mature-dormant
    public TileBase[] grassDryFlowerTiles = new TileBase[3];       // young, medium, mature
    public TileBase[] grassBalancedFlowerTiles = new TileBase[3];  // young, medium, mature
    public TileBase[] grassWetFlowerTiles = new TileBase[3];       // young, medium, mature
    public TileBase[] grassDrySeedTiles = new TileBase[3];         // young, medium, mature
    public TileBase[] grassBalancedSeedTiles = new TileBase[3];    // young, medium, mature
    public TileBase[] grassWetSeedTiles = new TileBase[3];         // young, medium, mature

    [Header("Map Settings")]
    public int width = 30;
    public int height = 30;

    [Header("Environment Reference")]
    public EnvironmentManager environmentManager;
    public EnvironmentVisualizer environmentVisualizer;
    public GrassManager grassManager;
    public GrassDataPanelController grassDataPanelController;
    public DetailedInformationPanelController detailedInformationPanelController;
    public CameraController cameraController;

    private HashSet<Vector3Int> changedTiles = new HashSet<Vector3Int>();
    private EnvironmentData[,] tileEnvironmentData;
    private int offsetX;
    private int offsetY;
    private List<Vector3Int> allTilePositions = new List<Vector3Int>();
    private bool isSelecting = false;

    public float hoverThreshold = 0.5f; // Time in seconds to register a hover
    private Vector3Int lastTilePos;
    private float hoverTimer = 0f;
    private bool isHovering = false;
    private bool isShowing = false;
    private EnvironmentData showData;
    private Vector3Int showPos;
    private bool rightClickPending = false;
    private Vector3 rightClickStartScreen;
    private const float RIGHT_CLICK_DRAG_THRESHOLD = 8f;
    internal bool allowTileModification = true; // Flag to enable/disable tile modification
    private bool isInitialized = false; // Set once the grid exists, so a load applied before Start() is not wiped

    void Awake()
    {
        if (SimulationStartupSettings.TryGet(out int startupWidth, out int startupHeight, out _, out _, out _))
        {
            width = startupWidth;
            height = startupHeight;
        }
    }

    void Start()
    {
        isSelecting = false;
        isShowing = false;
        EnsureInitialized();
    }

    /// <summary>
    /// Builds the default map if nothing (e.g. a snapshot load) has built it yet.
    /// Safe to call from other managers' Start() regardless of script execution order.
    /// </summary>
    internal void EnsureInitialized()
    {
        if (!isInitialized)
        {
            InitializeMap();
            // A fresh map (not a load, which brings its own atmosphere) starts at the reference CO2 level
            environmentManager.ResetAtmosphereForNewMap();
        }
    }

    void Update()
    {
        if (!allowTileModification)
            return;

        if (Input.GetMouseButtonDown(0) && !IsPointerOverUI() && !isSelecting) // Left-click to modify
        {
            isSelecting = true;
            Vector3 worldPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            Vector3Int gridPosition = terrainTilemap.WorldToCell(worldPosition);
            TileBase clickedTile = terrainTilemap.GetTile(gridPosition);
            changedTileIsLand = IsLandTile(clickedTile);
        }
        if (Input.GetMouseButtonUp(0))
        {
            isSelecting = false;
            changedTiles.Clear();
            changedTileIsLand = false;
        }

        CheckMouseHover();
        CheckRightClick();

        ChangeTile();
    }

    bool IsPointerOverUI()
    {
        // Check if the mouse is over a UI element
        return EventSystem.current.IsPointerOverGameObject();
    }

    void ChangeTile()
    {
        if (isSelecting)
        {
            Vector3 worldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            Vector3Int gridPos = terrainTilemap.WorldToCell(worldPos);
            // Get the current tile at this position
            TileBase currentTile = terrainTilemap.GetTile(gridPos);
            if (currentTile == null)
                return;
            // Check if this tile position has already been changed
            if (!changedTiles.Contains(gridPos))
            {
                bool currentIsLand = IsLandTile(currentTile);
                bool currentIsWater = currentTile == waterTile;
                if (!currentIsLand && !currentIsWater)
                    return;
                if (currentIsLand != changedTileIsLand)
                    return;

                // Toggle between land and water tiles
                if (currentIsLand)
                {
                    terrainTilemap.SetTile(gridPos, waterTile);
                    UpdateTileEnvironmentData(gridPos, true); // Update dictionary for water tile
                    // Trigger environment update to recalculate affected tiles
                    environmentManager.UpdateTileEnvironment(gridPos);
                }
                else
                {
                    terrainTilemap.SetTile(gridPos, GetRandomLandTile());
                    UpdateTileEnvironmentData(gridPos, false); // Update dictionary for land tile
                    // Trigger environment update to recalculate affected tiles
                    environmentManager.UpdateTileEnvironment(gridPos);
                }

                // Add this position to the changedTiles set
                changedTiles.Add(gridPos);

                if (IsValidTilePosition(gridPos))
                {
                    EnvironmentData target = GetTileEnvironmentData(gridPos);
                    if (target.grass != null)
                    {
                        grassManager.RemoveGrassInstance(gridPos, target, DeathCause.TerrainChange);
                    }
                }
            }
        }
    }

    void InitializeMap()
    {
        terrainTilemap.ClearAllTiles();
        overlayTilemap.ClearAllTiles();
        grassTilemap.ClearAllTiles();
        changedTiles.Clear();

        Vector3Int centerOffset = new Vector3Int(width / 2, height / 2, 0);

        offsetX = width / 2;
        offsetY = height / 2;

        tileEnvironmentData = new EnvironmentData[width, height];

        allTilePositions.Clear();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3Int tilePos = new Vector3Int(x, y, 0) - centerOffset;
                allTilePositions.Add(tilePos);

                terrainTilemap.SetTile(tilePos, waterTile);

                overlayTilemap.SetTile(tilePos, transparentTile);
                overlayTilemap.SetColor(tilePos, Color.clear);

                grassTilemap.SetTile(tilePos, transparentTile);
                grassTilemap.SetColor(tilePos, Color.clear);

                // Store directly into array
                EnvironmentData environmentData = new EnvironmentData
                {
                    radiation = environmentManager.waterRadiation,
                    temperature = 20f,
                    hydration = 100f,  // [FIXED] Water tiles start fully hydrated, not 50
                    isWater = true,
                    oxygen = environmentManager.waterOxygen,
                    cachedTile = transparentTile,
                    cachedTileColor = Color.clear,
                    // Cache neighbor position
                    immediateNeighbors = new List<Vector3Int>()
                };
                environmentManager.InitializeNutrientPools(environmentData, true);
                tileEnvironmentData[x, y] = environmentData;

                for (int i = -1; i <= 1; i++)
                {
                    for (int j = -1; j <= 1; j++)
                    {
                        if (i == 0 && j == 0)
                        {
                            continue;
                        }
                        Vector3Int neighborPos = tilePos + new Vector3Int(i, j, 0);
                        if (IsValidTilePosition(neighborPos))
                        {
                            tileEnvironmentData[x, y].immediateNeighbors.Add(neighborPos);
                        }
                    }
                }
            }
        }

        isInitialized = true;
        environmentManager.UpdateTileEnvironment(null, true);
        // [OPTIMIZED] Static temperature offsets cached during above fullUpdate
    }

    internal static void DisableTileModification()
    {
        MapManager instance = FindFirstObjectByType<MapManager>();
        if (instance != null)
        {
            instance.allowTileModification = !instance.allowTileModification; // Toggle tile modification
        }
    }

    internal void ReinitializeForLoad(int savedWidth, int savedHeight)
    {
        width = Mathf.Max(1, savedWidth);
        height = Mathf.Max(1, savedHeight);
        InitializeMap();
    }

    internal void SetTileWaterStateForLoad(Vector3Int tilePos, bool isWater)
    {
        if (!IsValidTilePosition(tilePos))
            return;

        int x = tilePos.x + offsetX;
        int y = tilePos.y + offsetY;

        EnvironmentData data = tileEnvironmentData[x, y];
        data.isWater = isWater;
        environmentManager.InitializeNutrientPools(data, isWater);
        terrainTilemap.SetTile(tilePos, isWater ? waterTile : GetRandomLandTile());

        if (isWater)
        {
            data.oxygen = environmentManager.waterOxygen;
            data.radiation = environmentManager.waterRadiation;
        }
        else
        {
            data.oxygen = environmentManager.landOxygen;
            data.radiation = environmentManager.landRadiation;
        }

        data.cachedTile = transparentTile;
        data.cachedTileColor = new Color(-1, -1, -1, -1);
        data.cachedVisualizationColor = new Color(-1, -1, -1, -1);
        data.visualizationTileFlagsSet = false;
    }

    void UpdateTileEnvironmentData(Vector3Int tilePos, bool isWater)
    {
        int x = tilePos.x + offsetX;
        int y = tilePos.y + offsetY;

        tileEnvironmentData[x, y].isWater = isWater;
        environmentManager.InitializeNutrientPools(tileEnvironmentData[x, y], isWater);

        // Update the tile's environmental data
        if (isWater)
        {
            tileEnvironmentData[x, y].oxygen = environmentManager.waterOxygen;
            tileEnvironmentData[x, y].radiation = environmentManager.waterRadiation;
        }
        else
        {
            tileEnvironmentData[x, y].oxygen = environmentManager.landOxygen;
            tileEnvironmentData[x, y].radiation = environmentManager.landRadiation;
        }

        // Invalidate cached color so it will be recalculated when tile type changes
        tileEnvironmentData[x, y].cachedTileColor = new Color(-1, -1, -1, -1); // Invalid color marker

        environmentManager.UpdateTileEnvironment(tilePos); // Trigger environment update for this specific tile
    }

    public EnvironmentData GetTileEnvironmentData(Vector3Int pos)
    {
        if (!IsValidTilePosition(pos))
            throw new System.IndexOutOfRangeException("Invalid tile position");

        int x = pos.x + offsetX;
        int y = pos.y + offsetY;

        return tileEnvironmentData[x, y];
    }

    public List<Vector3Int> GetAllTilePositions()
    {
        return allTilePositions; // Return cached list
    }

    /// <summary>
    /// Returns a list of valid tile positions within a given Manhattan radius around a center (inclusive).
    /// </summary>
    public List<Vector3Int> GetTilePosOfRange(Vector3Int center, int radius)
    {
        return GetTilePosOfRange(center, radius, new List<Vector3Int>());
    }

    /// <summary>
    /// Non-allocating overload: appends positions to the caller's (e.g. pooled) list and returns it.
    /// </summary>
    public List<Vector3Int> GetTilePosOfRange(Vector3Int center, int radius, List<Vector3Int> results)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                // Manhattan distance check
                if (Mathf.Abs(dx) + Mathf.Abs(dy) > radius) continue;

                Vector3Int pos = center + new Vector3Int(dx, dy, 0);
                results.Add(pos);
            }
        }
        return results;
    }

    public void SetOverlayColor(Vector3Int tilePos, Color color, EnvironmentData data = null)
    {
        // [OPTIMIZED] Only set tile flags once per tile, not every frame
        // SetTileFlags is an expensive operation, so we track whether it's been done
        if (data == null || !data.visualizationTileFlagsSet)
        {
            overlayTilemap.SetTileFlags(tilePos, TileFlags.None);
            if (data != null)
            {
                data.visualizationTileFlagsSet = true;
            }
        }
        
        // SetColor is still needed every frame, but compared to SetTileFlags it's cheaper
        overlayTilemap.SetColor(tilePos, color);
    }

    /// <summary>
    /// Updates grass tile and color for a given tile based on its EnvironmentData.
    /// </summary>
    public void UpdateGrassTile(EnvironmentData data, Vector3Int gridPosition)
    {
        if (data == null)
            return;

        if (data.grass == null)
        {
            if (data.cachedTile != transparentTile || data.cachedTileColor != Color.clear)
            {
                grassTilemap.SetTile(gridPosition, transparentTile);
                data.cachedTile = transparentTile;
                grassTilemap.SetColor(gridPosition, Color.clear);
                data.cachedTileColor = Color.clear;
            }
            return;
        }

        string plantType = data.grass.GetCachedPlantType();
        TileBase grassStageTypeTile = GetGrassStageTypeTile(
            data.grass.grassDensity,
            plantType,
            data.grass.IsDormant,
            data.grass.IsFlowering,
            data.grass.IsSeeding);
        if (grassStageTypeTile == null)
        {
            grassStageTypeTile = grassTile;
        }

        Color grassColor = Color.white;
        if (!data.grass.IsDormant)
        {
            grassColor = new Color(
                data.grass.genome.GetTraitValue(TraitId.ColorR),
                data.grass.genome.GetTraitValue(TraitId.ColorG),
                data.grass.genome.GetTraitValue(TraitId.ColorB));
        }

        grassColor.a = data.isWater ? 0.8f : 1f;

        if (data.cachedTile != grassStageTypeTile)
        {
            grassTilemap.SetTile(gridPosition, grassStageTypeTile);
            data.cachedTile = grassStageTypeTile;
        }

        if (data.cachedTileColor != grassColor)
        {
            grassTilemap.SetColor(gridPosition, grassColor);
            data.cachedTileColor = grassColor;
        }
    }

    /// <summary>
    /// Gets the appropriate grass tile based on growth stage and plant type.
    /// Returns null if no tile is assigned for the current state.
    /// </summary>
    public TileBase GetGrassStageTypeTile(float grassDensity, string plantType, bool isDormant, bool isFlowering = false, bool isSeeding = false)
    {
        // Determine growth stage from density
        int stageIndex;
        if (grassDensity < 0.3f)
            stageIndex = 0; // young
        else if (grassDensity < 0.7f)
            stageIndex = 1; // medium
        else
            stageIndex = 2; // mature

        // Determine plant type and get the corresponding tile array
        TileBase[] plantTypeTiles = plantType switch
        {
            "dryTolerant" => grassDryTiles,
            "wetTolerant" => grassWetTiles,
            "balanced" => grassBalancedTiles,
            _ => grassBalancedTiles
        };

        // Priority: dormancy > seeding > flowering > normal growth
        if (isDormant && plantTypeTiles != null && stageIndex + 3 < plantTypeTiles.Length && plantTypeTiles[stageIndex] != null)
            return plantTypeTiles[stageIndex + 3];
        
        TileBase[] seedTiles = plantType switch
        {
            "dryTolerant" => grassDrySeedTiles,
            "wetTolerant" => grassWetSeedTiles,
            "balanced" => grassBalancedSeedTiles,
            _ => grassBalancedSeedTiles
        };

        if (isSeeding && seedTiles != null && stageIndex < seedTiles.Length && seedTiles[stageIndex] != null)
            return seedTiles[stageIndex];

        TileBase[] flowerTiles = plantType switch
        {
            "dryTolerant" => grassDryFlowerTiles,
            "wetTolerant" => grassWetFlowerTiles,
            "balanced" => grassBalancedFlowerTiles,
            _ => grassBalancedFlowerTiles
        };

        if (isFlowering && flowerTiles != null && stageIndex < flowerTiles.Length && flowerTiles[stageIndex] != null)
            return flowerTiles[stageIndex];

        if (plantTypeTiles != null && stageIndex < plantTypeTiles.Length && plantTypeTiles[stageIndex] != null)
            return plantTypeTiles[stageIndex];

        return null;
    }

    public void ResetOverlay()
    {
        // [OPTIMIZED] Reset all overlay tiles to transparent and clear visualization caches
        foreach (var tilePos in allTilePositions)
        {
            overlayTilemap.SetTileFlags(tilePos, TileFlags.None);
            overlayTilemap.SetColor(tilePos, Color.clear);
            
            // Clear visualization caches so next visualization updates all tiles fresh
            EnvironmentData data = GetTileEnvironmentData(tilePos);
            if (data != null)
            {
                data.cachedVisualizationColor = new Color(-1, -1, -1, -1);  // Invalid color
                data.visualizationTileFlagsSet = false;
            }
        }
    }

    private void CheckMouseHover()
    {
        // Get mouse position in world space and convert to tile position
        Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector3Int currentTilePos = terrainTilemap.WorldToCell(mouseWorldPos);

        // If the mouse is over the same tile as last frame, increment the timer
        if (currentTilePos == lastTilePos)
        {
            hoverTimer += Time.unscaledDeltaTime;
            if (!isHovering && hoverTimer >= hoverThreshold && !IsPointerOverUI())
            {
                isHovering = true;
                if (IsValidTilePosition(currentTilePos))
                {
                    EnvironmentData tileData = GetTileEnvironmentData(currentTilePos);
                    if (tileData.grass != null)
                    {
                        isShowing = true;
                        showData = tileData;
                        showPos = currentTilePos;
                    }
                }
            }
        }
        else
        {
            // If we were hovering over a tile and now have moved away, call exit event
            if (isHovering)
            {
                if (grassDataPanelController != null)
                {
                    grassDataPanelController.Hide();
                }
            }
            // Reset the timer and update the current tile position
            lastTilePos = currentTilePos;
            hoverTimer = 0f;
            isHovering = false;
            isShowing = false;
        }

        if (isShowing)
        {
            if (grassDataPanelController != null)
            {
                grassDataPanelController.ShowGrassData(showData, showPos);
            }
        }
    }

    private void CheckRightClick()
    {
        if (Input.GetMouseButtonDown(1))
        {
            if (IsPointerOverUI())
                return;

            rightClickPending = true;
            rightClickStartScreen = Input.mousePosition;
        }

        if (rightClickPending && Input.GetMouseButton(1))
        {
            if ((Input.mousePosition - rightClickStartScreen).sqrMagnitude > RIGHT_CLICK_DRAG_THRESHOLD * RIGHT_CLICK_DRAG_THRESHOLD)
            {
                rightClickPending = false;
            }
        }

        if (Input.GetMouseButtonUp(1))
        {
            if (!rightClickPending)
                return;

            rightClickPending = false;

            Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            Vector3Int tilePos = terrainTilemap.WorldToCell(mouseWorldPos);

            if (!IsValidTilePosition(tilePos))
            {
                if (detailedInformationPanelController != null)
                {
                    detailedInformationPanelController.Hide();
                }
                return;
            }

            EnvironmentData tileData = GetTileEnvironmentData(tilePos);
            if (detailedInformationPanelController != null)
            {
                detailedInformationPanelController.ShowDetailedInfo(tileData, tilePos);
            }
            if (grassDataPanelController != null)
            {
                grassDataPanelController.Hide();
            }

            // Center camera on the tile while maintaining zoom
            if (cameraController != null)
            {
                Vector3 tileWorldPos = terrainTilemap.CellToWorld(tilePos);
                // Center on the tile (add half tile size to center it properly)
                tileWorldPos += terrainTilemap.cellSize * 0.5f;
                cameraController.CenterOnPosition(tileWorldPos);
            }
        }
    }

    internal bool IsValidTilePosition(Vector3Int tilePos)
    {
        int x = tilePos.x + offsetX;
        int y = tilePos.y + offsetY;

        if (x < 0 || x >= width || y < 0 || y >= height)
            return false;

        return true;
    }

    public Vector2Int ToArrayCoords(Vector3Int tilePos)
    {
        return new Vector2Int(tilePos.x + offsetX, tilePos.y + offsetY);
    }

    private bool IsLandTile(TileBase tile)
    {
        if (tile == null)
            return false;
        if (tile == landTile)
            return true;
        if (landTiles == null || landTiles.Length == 0)
            return false;
        for (int i = 0; i < landTiles.Length; i++)
        {
            if (tile == landTiles[i])
                return true;
        }
        return false;
    }

    private TileBase GetRandomLandTile()
    {
        if (landTiles != null && landTiles.Length > 0)
        {
            for (int attempt = 0; attempt < landTiles.Length; attempt++)
            {
                int index = Random.Range(0, landTiles.Length);
                if (landTiles[index] != null)
                    return landTiles[index];
            }
        }
        return landTile;
    }
}
