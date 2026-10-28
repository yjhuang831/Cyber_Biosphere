using UnityEngine;

public class GrassSpawnInput : MonoBehaviour
{
    [Header("References")]
    public MapManager mapManager;
    public GrassManager grassManager;

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.G))
        {
            if (mapManager == null || grassManager == null || !mapManager.allowTileModification)
                return;

            Vector3 worldPosition = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            Vector3Int gridPosition = mapManager.terrainTilemap.WorldToCell(worldPosition);
            if (mapManager.IsValidTilePosition(gridPosition))
            {
                EnvironmentData data = mapManager.GetTileEnvironmentData(gridPosition);
                if (data.grass == null)
                {
                    PlantGenome genome = null;
                    float initialEnergy = 0f;

                    // Check if a saved species is selected
                    if (GenusManager.Instance != null && !string.IsNullOrEmpty(GenusManager.Instance.selectedSpeciesId))
                    {
                        // Try to load the selected species genome
                        if (GenusManager.Instance.TryGetSavedSpeciesGenome(GenusManager.Instance.selectedSpeciesId, out PlantGenomeState genomeState))
                        {
                            // Convert PlantGenomeState to PlantGenome (carries the species GUID into the simulation)
                            genome = grassManager.BuildGenomeFromState(genomeState);
                            if (genome != null)
                            {
                                initialEnergy = genome.GetTraitValue(TraitId.SpreadCost);
                            }
                        }
                    }

                    // Spawn with loaded genome, or null for random if no selection
                    grassManager.SpawnNewGrass(data, gridPosition, genome, initialEnergy); // Pass initial energy
                }
            }
        }
    }
}
