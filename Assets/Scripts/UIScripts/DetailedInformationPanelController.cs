using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Text;

public class DetailedInformationPanelController : MonoBehaviour
{
    [Header("References")]
    public MapManager mapManager;
    public SeedManager seedManager;
    public Canvas canvas;

    [Header("UI Elements")]
    public GameObject tileInfoPanel;
    public GameObject seedInfoPanel;
    public GameObject grassInfoPanel;
    public GameObject extraInfoPanel;
    public GameObject[] tabButtons;
    public GameObject quitButton;
    public Button saveSpeciesButton;
    public GameObject saveSpeciesPanel;
    public TMP_InputField genusInputField;
    public TMP_InputField speciesInputField;
    public Button cancelSaveButton;
    public Button confirmSaveButton;
    public RectTransform highlightRect;
    public TMP_Text tileInfoText;
    public TMP_Text seedInfoText;
    public TMP_Text grassInfoText;
    public TMP_Text extraInfoText;

    [Header("Refresh")]
    public bool autoRefresh = true;
    public float refreshInterval = 0.25f;

    private EnvironmentData currentData;
    private Vector3Int currentPos;
    private bool isOpen = false;
    private float refreshTimer = 0f;
    private TimeController timeController; // Found lazily; supplies germination steps per day for seed estimates
    private const float LIGHT_ACTIVITY_THRESHOLD = 0.01f; // Same daylight cut-off as GrassInstance.PerformMetabolism
    private const float DISPLAY_LUX_PER_LIGHT_UNIT = 100000f;
    private const float DISPLAY_USVH_PER_RADIATION_UNIT = 0.6f;

    void Start()
    {
        HideAllPanels();
        if (saveSpeciesPanel != null)
            saveSpeciesPanel.SetActive(false);
        if (saveSpeciesButton != null)
            saveSpeciesButton.onClick.AddListener(OpenSaveSpeciesPanel);
        if (cancelSaveButton != null)
            cancelSaveButton.onClick.AddListener(CloseSaveSpeciesPanel);
        if (confirmSaveButton != null)
            confirmSaveButton.onClick.AddListener(ConfirmSaveSpecies);
    }

    void Update()
    {
        if (!isOpen)
            return;

        UpdateHighlightPosition();

        if (!autoRefresh)
            return;

        refreshTimer += Time.unscaledDeltaTime;
        if (refreshTimer >= refreshInterval)
        {
            RefreshAllTexts();
            refreshTimer = 0f;
        }
    }

    public void ShowDetailedInfo(EnvironmentData data, Vector3Int gridPosition)
    {
        if (data == null)
            return;

        currentData = data;
        currentPos = gridPosition;
        isOpen = true;
        refreshTimer = 0f;

        RefreshAllTexts();
        ShowDefaultTab();
        SetHighlightActive(true);
    }

    public void Hide()
    {
        isOpen = false;
        SetHighlightActive(false);
        HideAllPanels();
    }

    public void ShowTileTab()
    {
        SetActiveTab(tileInfoPanel);
    }

    public void ShowSeedTab()
    {
        SetActiveTab(seedInfoPanel);
    }

    public void ShowGrassTab()
    {
        SetActiveTab(grassInfoPanel);
    }

    public void ShowExtraTab()
    {
        SetActiveTab(extraInfoPanel);
    }

    private void SetActiveTab(GameObject active)
    {
        SetButtonsActive(true);
        if (tileInfoPanel != null) tileInfoPanel.SetActive(active == tileInfoPanel);
        if (seedInfoPanel != null) seedInfoPanel.SetActive(active == seedInfoPanel);
        if (grassInfoPanel != null) grassInfoPanel.SetActive(active == grassInfoPanel);
        if (extraInfoPanel != null) extraInfoPanel.SetActive(active == extraInfoPanel);
    }

    private void HideAllPanels()
    {
        if (tileInfoPanel != null) tileInfoPanel.SetActive(false);
        if (seedInfoPanel != null) seedInfoPanel.SetActive(false);
        if (grassInfoPanel != null) grassInfoPanel.SetActive(false);
        if (extraInfoPanel != null) extraInfoPanel.SetActive(false);
        SetButtonsActive(false);
    }

    private void ShowDefaultTab()
    {
        HideAllPanels();
        SetButtonsActive(true);
        if (tileInfoPanel != null)
        {
            SetActiveTab(tileInfoPanel);
            return;
        }
        if (seedInfoPanel != null)
        {
            SetActiveTab(seedInfoPanel);
            return;
        }
        if (grassInfoPanel != null)
        {
            SetActiveTab(grassInfoPanel);
            return;
        }
        if (extraInfoPanel != null)
        {
            SetActiveTab(extraInfoPanel);
        }
    }

    public void QuitPanel()
    {
        CloseSaveSpeciesPanel();
        Hide();
    }

    public void OpenSaveSpeciesPanel()
    {
        if (currentData == null || currentData.grass == null || currentData.grass.genome == null)
        {
            Debug.LogWarning("Cannot save species: no grass on selected tile.");
            return;
        }

        if (saveSpeciesPanel != null)
        {
            saveSpeciesPanel.SetActive(true);
            if (genusInputField != null)
                genusInputField.text = currentData.grass.genome.genus ?? "";
            if (speciesInputField != null)
                speciesInputField.text = currentData.grass.genome.species ?? "";
        }
    }

    public void CloseSaveSpeciesPanel()
    {
        if (saveSpeciesPanel != null)
            saveSpeciesPanel.SetActive(false);
    }

    public void ConfirmSaveSpecies()
    {
        if (currentData == null || currentData.grass == null || currentData.grass.genome == null)
        {
            Debug.LogWarning("Cannot save species: no grass on selected tile.");
            return;
        }

        string genus = genusInputField != null ? genusInputField.text.Trim() : "";
        string species = speciesInputField != null ? speciesInputField.text.Trim() : "";

        if (string.IsNullOrWhiteSpace(genus) || string.IsNullOrWhiteSpace(species))
        {
            Debug.LogWarning("Cannot save species: genus and species names are required.");
            return;
        }

        if (GenusManager.Instance != null)
        {
            // A clashing name is saved with a numeric suffix; log the name actually used
            bool success = GenusManager.Instance.SaveLocalSpeciesGenome(currentData.grass.genome, genus, species, out GenusManager.SpeciesEntry savedEntry);
            if (success)
            {
                // The saved plant becomes the species' founding specimen in this simulation
                if (mapManager != null && mapManager.grassManager != null)
                {
                    mapManager.grassManager.AssignSpecimenToSpecies(currentData.grass, savedEntry.guid, savedEntry.genus, savedEntry.species);
                }
                Debug.Log("Species saved: " + savedEntry.DisplayName);
            }
        }

        CloseSaveSpeciesPanel();
    }

    private void RefreshAllTexts()
    {
        UpdateTileInfo();
        UpdateSeedInfo();
        UpdateGrassInfo();
        UpdateExtraInfo();
    }

    private void UpdateTileInfo()
    {
        if (tileInfoText == null || currentData == null)
            return;

        string terrain = currentData.isWater ? "Water" : "Land";
        Vector2Int displayPos = GetDisplayTilePosition();
        tileInfoText.text =
            "Pos: " + displayPos +
            "\nTerrain: " + terrain +
            "\nTemp: " + FormatTemperatureCelsius(currentData.temperature) +
            "\nHydration: " + FormatPercent(currentData.hydration) +
            "\nLight: " + FormatLux(currentData.light) +
            "\nRadiation: " + FormatRadiationRate(currentData.radiation) +
            "\nOxygen: " + FormatPercent(currentData.oxygen);
    }

    private void UpdateSeedInfo()
    {
        if (seedInfoText == null)
            return;

        if (seedManager == null || !seedManager.activeSeeds.TryGetValue(currentPos, out List<Seed> seeds))
        {
            seedInfoText.text = "Seeds: 0";
            return;
        }

        int count = seeds.Count;
        string result = "Seeds: " + count + "\n";

        for (int i = 0; i < count; i++)
        {
            Seed seed = seeds[i];
            if (i > 0)
                result += "\n";

            result += "[" + (i + 1) + "] " +
                "Stored: " + seed.storedEnergy.ToString("F2") +
                " | Germ: " + seed.germinationEnergy.ToString("F2") +
                " | Stage: " + seed.germinationStage + "/" + seed.sproutStage +
                (seed.germinationCue > 0f ? " | Cue: " + seed.germinationCue.ToString("F2") : "") +
                (seed.coldPreservation > 0f ? " | Cold: " + FormatPercent(seed.coldPreservation * 100f) : "");
        }

        seedInfoText.text = result;
    }

    private void UpdateGrassInfo()
    {
        if (grassInfoText == null || currentData == null)
            return;

        GrassInstance grass = currentData.grass;
        if (grass == null || grass.genome == null)
        {
            grassInfoText.text = "No plant on this tile.";
            if (saveSpeciesButton != null)
                saveSpeciesButton.gameObject.SetActive(false);
            return;
        }

        // Show save button when there's grass
        if (saveSpeciesButton != null)
            saveSpeciesButton.gameObject.SetActive(true);

        if (timeController == null)
            timeController = FindFirstObjectByType<TimeController>();
        GrassManager grassManager = mapManager != null ? mapManager.grassManager : null;
        grassInfoText.text = BuildPlantReport(grass, currentData, grassManager, timeController);
    }

    private void UpdateExtraInfo()
    {
        if (extraInfoText == null)
            return;

        if (currentData == null || currentData.grass == null || currentData.grass.genome == null)
        {
            extraInfoText.text = "DNA: none.";
            return;
        }

        PlantGenome genome = currentData.grass.genome;
        List<Allele> maternal = genome.maternalDNA;
        List<Allele> paternal = genome.paternalDNA;
        int loci = Mathf.Max(maternal != null ? maternal.Count : 0, paternal != null ? paternal.Count : 0);
        if (loci == 0)
        {
            extraInfoText.text = "DNA: empty.";
            return;
        }

        extraInfoText.text = BuildVerticalGeneDisplay(genome, loci, maternal, paternal);
    }

    private string BuildVerticalGeneDisplay(PlantGenome genome, int loci, List<Allele> maternal, List<Allele> paternal)
    {
        // Group alleles by trait ID since maternal and paternal may have different structures
        Dictionary<int, List<Allele>> maternalByTrait = GroupAllelesByTrait(maternal);
        Dictionary<int, List<Allele>> paternalByTrait = GroupAllelesByTrait(paternal);

        // Collect all unique trait IDs
        SortedSet<int> allTraitIds = new SortedSet<int>();
        if (maternalByTrait != null)
            foreach (var traitId in maternalByTrait.Keys)
                allTraitIds.Add(traitId);
        if (paternalByTrait != null)
            foreach (var traitId in paternalByTrait.Keys)
                allTraitIds.Add(traitId);

        TraitDatabase db = TraitDatabase.I;
        string result = "DNA Loci: " + loci + "\n\n";

        foreach (int traitId in allTraitIds)
        {
            // Get trait name
            string traitName = ResolveTraitNameFromDatabase(traitId, db);
            result += "<b>" + traitName + " [" + FormatTraitId(traitId) + "]</b>\n";

            List<Allele> matAlleles = maternalByTrait != null && maternalByTrait.ContainsKey(traitId) ? maternalByTrait[traitId] : new List<Allele>();
            List<Allele> patAlleles = paternalByTrait != null && paternalByTrait.ContainsKey(traitId) ? paternalByTrait[traitId] : new List<Allele>();
            int maxLociForTrait = Mathf.Max(matAlleles.Count, patAlleles.Count);

            // Display each locus of this trait
            for (int locusIndex = 0; locusIndex < maxLociForTrait; locusIndex++)
            {
                string locusLine = "  [" + locusIndex + "] ";

                // Maternal allele
                if (locusIndex < matAlleles.Count)
                {
                    Allele mat = matAlleles[locusIndex];
                    string matColor = mat.dominance ? "<color=#00FF00>" : "<color=#FFFF00>";
                    locusLine += matColor + FormatAlleleValue(mat) + "</color> ";
                }
                else
                {
                    locusLine += "-- ";
                }

                // Paternal allele
                if (locusIndex < patAlleles.Count)
                {
                    Allele pat = patAlleles[locusIndex];
                    string patColor = pat.dominance ? "<color=#00FF00>" : "<color=#FFFF00>";
                    locusLine += patColor + FormatAlleleValue(pat) + "</color>";
                }
                else
                {
                    locusLine += "--";
                }

                result += locusLine + "\n";
            }

            // Show phenotype formula
            result += FormatPhenotypeFormula(traitId, genome, db) + "\n\n";
        }

        return result.TrimEnd();
    }

    private string FormatPhenotypeFormula(int traitId, PlantGenome genome, TraitDatabase db)
    {
        if (genome == null || genome.phenotypes == null || traitId < 0 || traitId >= genome.phenotypes.Length)
            return "  Phenotype: -- (range: unknown)";

        float phenotypeValue = genome.phenotypes[traitId];
        TraitDefinition trait = null;

        if (db != null && db.traits != null)
        {
            for (int i = 0; i < db.traits.Length; i++)
            {
                TraitDefinition def = db.traits[i];
                if (def == null) continue;
                int id = def.id >= 0 ? def.id : i;
                if (id == traitId)
                {
                    trait = def;
                    break;
                }
            }
        }

        if (trait == null)
            return "  Phenotype: <b>" + phenotypeValue.ToString("F2") + "</b> (range: unknown)";

        return "  Phenotype: <b>" + phenotypeValue.ToString("F2") + "</b> (range: " + trait.minValue.ToString("F1") + " to " + trait.maxValue.ToString("F1") + ")";
    }

    private string ResolveTraitNameFromDatabase(int traitId, TraitDatabase db)
    {
        if (db != null && db.traits != null)
        {
            for (int i = 0; i < db.traits.Length; i++)
            {
                TraitDefinition def = db.traits[i];
                if (def == null) continue;
                int id = def.id >= 0 ? def.id : i;
                if (id == traitId)
                {
                    return string.IsNullOrEmpty(def.traitName) ? "Trait " + FormatTraitId(traitId) : def.traitName;
                }
            }
        }
        return "Trait " + FormatTraitId(traitId);
    }

    private Dictionary<int, List<Allele>> GroupAllelesByTrait(List<Allele> alleles)
    {
        if (alleles == null || alleles.Count == 0)
            return new Dictionary<int, List<Allele>>();

        Dictionary<int, List<Allele>> grouped = new Dictionary<int, List<Allele>>();
        for (int i = 0; i < alleles.Count; i++)
        {
            Allele allele = alleles[i];
            if (!grouped.ContainsKey(allele.traitId))
            {
                grouped[allele.traitId] = new List<Allele>();
            }
            grouped[allele.traitId].Add(allele);
        }
        return grouped;
    }

    private static string FormatAlleleValue(Allele allele)
    {
        return allele.value.ToString("00");
    }

    private static string FormatTraitId(int traitId)
    {
        if (traitId < 0)
            return "--";
        if (traitId < 10)
            return "0" + traitId;
        return traitId.ToString();
    }

    private Vector2Int GetDisplayTilePosition()
    {
        if (mapManager != null)
        {
            return mapManager.ToArrayCoords(currentPos);
        }
        return new Vector2Int(Mathf.Abs(currentPos.x), Mathf.Abs(currentPos.y));
    }

    private void UpdateHighlightPosition()
    {
        if (highlightRect == null || canvas == null || mapManager == null)
            return;

        Vector3 worldPos = mapManager.terrainTilemap.GetCellCenterWorld(currentPos);
        Vector3 screenPos = Camera.main.WorldToScreenPoint(worldPos);

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform,
            screenPos,
            canvas.worldCamera,
            out Vector2 localPoint
        );

        highlightRect.localPosition = localPoint;
        UpdateHighlightSize(worldPos);
    }

    private void SetHighlightActive(bool active)
    {
        if (highlightRect != null)
        {
            highlightRect.gameObject.SetActive(active);
        }
    }

    private void UpdateHighlightSize(Vector3 cellCenterWorld)
    {
        Vector3 cellSize = mapManager.terrainTilemap.layoutGrid.cellSize;
        Vector3 worldA = cellCenterWorld + new Vector3(cellSize.x * 0.5f, cellSize.y * 0.5f, 0f);
        Vector3 worldB = cellCenterWorld - new Vector3(cellSize.x * 0.5f, cellSize.y * 0.5f, 0f);

        Vector2 localA;
        Vector2 localB;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform,
            Camera.main.WorldToScreenPoint(worldA),
            canvas.worldCamera,
            out localA
        );
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform,
            Camera.main.WorldToScreenPoint(worldB),
            canvas.worldCamera,
            out localB
        );

        Vector2 size = new Vector2(Mathf.Abs(localA.x - localB.x), Mathf.Abs(localA.y - localB.y));
        if (size.sqrMagnitude > 0.001f)
        {
            highlightRect.sizeDelta = size;
        }
    }

    // ---- Plant tab ----
    // Every number here comes from the simulation's own formulas (GrassInstance.CalculatePhotosynthesis and
    // EstimateEnergyBudget), so the panel cannot drift from what the plant actually does.
    // Energy rates are per sim second; durations are in sim hours/days.

    private const float DEFAULT_METABOLISM_COST_PER_ALLELE = 0.01f;
    private const float DEFAULT_BASE_GROWTH_COST = 0.5f;
    private const float POOR_FIT = 0.45f;
    private const string GOOD_COLOR = "#00DD66";
    private const string WARN_COLOR = "#FFCC33";
    private const string BAD_COLOR = "#FF6666";
    private const string MUTED_COLOR = "#999999";

    private static string BuildPlantReport(GrassInstance grass, EnvironmentData envData, GrassManager grassManager, TimeController timeController)
    {
        float costPerGene = grassManager != null ? grassManager.metabolismCostPerAllele : DEFAULT_METABOLISM_COST_PER_ALLELE;
        float baseGrowthCost = grassManager != null ? grassManager.baseGrowthCost : DEFAULT_BASE_GROWTH_COST;
        GrassInstance.EnergyBudget budget = grass.EstimateEnergyBudget(envData, costPerGene, baseGrowthCost);
        GrassInstance.PhotosynthesisFactors factors = grass.CalculatePhotosynthesis(envData);

        StringBuilder text = new StringBuilder(2048);
        AppendIdentity(text, grass);
        text.Append("\n<b>NOW</b>\n");
        AppendCondition(text, grass, envData, budget, factors);
        text.Append("\n<b>HABITAT FIT</b> ").Append(Colored(MUTED_COLOR, "(100% = ideal for this plant)")).Append('\n');
        AppendHabitat(text, grass, envData, factors);
        text.Append("\n<b>ENERGY PER SECOND</b>\n");
        AppendEnergyBudget(text, grass, envData, budget);
        text.Append("\n<b>REPRODUCTION</b>\n");
        AppendReproduction(text, grass, grassManager, timeController);
        return text.ToString().TrimEnd();
    }

    private static void AppendIdentity(StringBuilder text, GrassInstance grass)
    {
        PlantGenome genome = grass.genome;
        bool named = !string.IsNullOrWhiteSpace(genome.genus) && !string.IsNullOrWhiteSpace(genome.species);
        string title = named ? genome.genus.Trim() + " " + genome.species.Trim() : "Unnamed plant";
        text.Append("<b><size=115%>").Append(title).Append("</size></b>\n");

        string lineage = ResolveLineageName(genome.speciesGuid);
        if (lineage != null && lineage != title)
        {
            text.Append("Descended from library species ").Append(lineage).Append('\n');
        }

        string ancestry = string.IsNullOrWhiteSpace(genome.origin) ? "unknown" : genome.origin.Trim();
        text.Append("Ancestry: ").Append(ancestry).Append(" · Generation ").Append(Mathf.Max(0, genome.generation)).Append('\n');
        text.Append(DescribePlantType(genome.GetPlantType())).Append(" · ").Append(genome.GetDominantDNALocusCount()).Append(" genes\n");
    }

    private static string ResolveLineageName(string speciesGuid)
    {
        if (string.IsNullOrEmpty(speciesGuid))
            return null;
        GenusManager genusManager = FindFirstObjectByType<GenusManager>();
        if (genusManager != null && genusManager.TryGetSpeciesEntry(speciesGuid, out GenusManager.SpeciesEntry entry))
            return entry.DisplayName;
        return "(no longer in library)";
    }

    private static string DescribePlantType(string plantType)
    {
        switch (plantType)
        {
            case "dryTolerant": return "Dry-adapted (needs < 30% water)";
            case "wetTolerant": return "Wet-adapted (needs > 70% water)";
            default: return "Moderate water needs";
        }
    }

    private static void AppendCondition(StringBuilder text, GrassInstance grass, EnvironmentData envData,
        GrassInstance.EnergyBudget budget, GrassInstance.PhotosynthesisFactors factors)
    {
        text.Append("  State: ").Append(DescribeState(grass)).Append('\n');
        text.Append("  Age: ").Append(grass.age).Append(grass.age == 1 ? " day\n" : " days\n");
        text.Append("  Energy: ").Append(CreateBar(grass.energyStored / 100f, 12)).Append(' ')
            .Append(grass.energyStored.ToString("F1")).Append(" / 100, ").Append(DescribeTrend(grass, budget)).Append('\n');
        text.Append("  Size: ").Append(CreateBar(grass.grassDensity, 12)).Append(' ')
            .Append((grass.grassDensity * 100f).ToString("F0")).Append("% (").Append(GetDensityStage(grass.grassDensity)).Append(")\n");

        List<string> warnings = CollectWarnings(grass, envData, budget, factors);
        if (warnings.Count == 0)
        {
            text.Append("  ").Append(Colored(GOOD_COLOR, "No problems.")).Append('\n');
        }
        for (int i = 0; i < warnings.Count; i++)
        {
            text.Append("  ").Append(Colored(BAD_COLOR, "! " + warnings[i])).Append('\n');
        }
    }

    private static string DescribeState(GrassInstance grass)
    {
        if (grass.IsDormant)
            return Colored(WARN_COLOR, "Dormant") + " - resting; costs cut to " + (grass.dormancyEffect * 100f).ToString("0.##") + "%; checks each dawn whether to wake";
        if (grass.IsSeeding)
            return Colored(GOOD_COLOR, "Seeding") + " - pollinated; part of its growth energy goes into seeds";
        if (grass.IsFlowering)
            return Colored(GOOD_COLOR, "Flowering") + " (day " + grass.floweringDays + ") - sending and receiving pollen";
        return "Active - growing and storing energy";
    }

    private static string DescribeTrend(GrassInstance grass, GrassInstance.EnergyBudget budget)
    {
        float net = budget.Net;
        if (net > 0.005f)
        {
            string full = grass.energyStored < 100f ? ", full in " + FormatSimDuration((100f - grass.energyStored) / net) : "";
            return Colored(GOOD_COLOR, "gaining " + FormatSigned(net) + "/s" + full);
        }
        if (net < -0.005f)
        {
            string wilt = grass.energyStored > WILTING_ENERGY ? ", wilts in " + FormatSimDuration((grass.energyStored - WILTING_ENERGY) / -net) : "";
            return Colored(BAD_COLOR, "losing " + FormatSigned(net) + "/s" + wilt);
        }
        return Colored(WARN_COLOR, "steady");
    }

    private const float WILTING_ENERGY = 5f; // Below this a plant shrinks instead of spending energy (GrassInstance.PerformMetabolism)

    private static List<string> CollectWarnings(GrassInstance grass, EnvironmentData envData,
        GrassInstance.EnergyBudget budget, GrassInstance.PhotosynthesisFactors factors)
    {
        List<string> warnings = new List<string>();
        PlantGenome genome = grass.genome;
        bool isDay = envData.light > LIGHT_ACTIVITY_THRESHOLD;

        if (grass.IsDormant)
        {
            warnings.Add("Dormant for " + grass.dormancyTimer + " of the " + grass.maxDormantTime + " days it can survive dormant");
            float wakeAbove = genome.GetTraitValue(TraitId.DormancyTrigger) * GrassInstance.DORMANCY_EXIT_MARGIN;
            float soFar = grass.DaylightPotentialAverage;
            warnings.Add("Wakes at dawn if today's daylight average beats " + wakeAbove.ToString("F2")
                + (soFar >= 0f ? " (so far " + soFar.ToString("F2") + ")" : " (no daylight yet today)"));
        }
        else if (grass.energyStored <= WILTING_ENERGY + 0.5f && grass.age > 1)
        {
            warnings.Add("Wilting: out of energy, losing 1% size per second");
        }

        if (factors.hydrationEfficiency < POOR_FIT)
        {
            warnings.Add(envData.hydration < genome.GetTraitValue(TraitId.HydrationRequirement) ? "Too dry for this plant" : "Too wet for this plant");
        }
        if (factors.temperatureMultiplier < POOR_FIT)
        {
            warnings.Add(envData.temperature < genome.GetTraitValue(TraitId.TemperatureTolerance) ? "Too cold for this plant" : "Too hot for this plant");
        }
        if (isDay && factors.lightEfficiency < POOR_FIT)
        {
            warnings.Add(envData.light < genome.GetTraitValue(TraitId.LightRequirement) ? "Too dark for this plant" : "Too bright for this plant");
        }

        // Dormancy is decided at dawn from the day's daylight average (GrassInstance.DailyDormancyDecision)
        float dormancyTrigger = genome.GetTraitValue(TraitId.DormancyTrigger);
        float daylightAverage = grass.DaylightPotentialAverage;
        if (!grass.IsDormant && dormancyTrigger > 0.0001f && daylightAverage >= 0f && grass.grassDensity >= grass.maturityThreshold && daylightAverage < dormancyTrigger)
        {
            warnings.Add("Today so far: " + daylightAverage.ToString("F2") + " vs dormancy trigger " + dormancyTrigger.ToString("F2") + " - goes dormant at dawn if it stays below");
        }

        if (budget.ageing > budget.geneUpkeep && budget.ageing > 0.001f)
        {
            warnings.Add("Old age: ageing now costs more than all its genes");
        }
        return warnings;
    }

    private static void AppendHabitat(StringBuilder text, GrassInstance grass, EnvironmentData envData, GrassInstance.PhotosynthesisFactors factors)
    {
        PlantGenome genome = grass.genome;
        float lightNeed = genome.GetTraitValue(TraitId.LightRequirement);
        float waterNeed = genome.GetTraitValue(TraitId.HydrationRequirement);
        float idealTemperature = genome.GetTraitValue(TraitId.TemperatureTolerance);
        bool isDay = envData.light > LIGHT_ACTIVITY_THRESHOLD;

        if (isDay)
        {
            string lightNote = factors.lightEfficiency >= 0.99f ? "" : envData.light < lightNeed ? " too dark" : " too bright";
            text.Append("  Light: ").Append(FormatPercent(envData.light * 100f)).Append(" of full sun, needs ").Append(FormatPercent(lightNeed * 100f))
                .Append(" -> ").Append(FormatScore(factors.lightEfficiency)).Append(lightNote).Append('\n');
        }
        else
        {
            text.Append("  Light: ").Append(Colored(MUTED_COLOR, "night - no photosynthesis")).Append('\n');
        }

        string waterNote = factors.hydrationEfficiency >= 0.99f ? "" : envData.hydration < waterNeed ? " too dry" : " too wet";
        text.Append("  Water: ").Append(FormatPercent(envData.hydration)).Append(", needs ").Append(FormatPercent(waterNeed))
            .Append(" -> ").Append(FormatScore(factors.hydrationEfficiency)).Append(waterNote).Append('\n');

        text.Append("  Temp: ").Append(FormatTemperatureCelsius(envData.temperature)).Append(", ideal ").Append(FormatTemperatureCelsius(idealTemperature))
            .Append(" -> x").Append(factors.temperatureMultiplier.ToString("F2")).Append(DescribeTemperatureEffect(envData.temperature - idealTemperature, factors.temperatureMultiplier)).Append('\n');

        // Weakest factor, among those that matter right now
        string weakest = "Water";
        float weakestScore = factors.hydrationEfficiency;
        if (Mathf.Clamp01(factors.temperatureMultiplier) < weakestScore)
        {
            weakest = "Temperature";
            weakestScore = factors.temperatureMultiplier;
        }
        if (isDay && factors.lightEfficiency < weakestScore)
        {
            weakest = "Light";
            weakestScore = factors.lightEfficiency;
        }
        text.Append("  Weakest: ").Append(weakest).Append(' ').Append(FormatScore(weakestScore)).Append('\n');
        text.Append("  ").Append(Colored(MUTED_COLOR, "Photosynthesis = 2 x water x temp x light x size")).Append('\n');
        text.Append("  ").Append(Colored(MUTED_COLOR, "Oxygen " + FormatPercent(envData.oxygen) + " (fuels night respiration) · Radiation "
            + envData.radiation.ToString("F2") + " (drives mutation)")).Append('\n');
    }

    private static string DescribeTemperatureEffect(float difference, float multiplier)
    {
        if (difference >= 0f && multiplier > 1.001f)
            return " warmth boost";
        if (difference >= 0f && multiplier < 0.999f)
            return " too hot";
        if (difference < 0f && multiplier < 0.999f)
            return " too cold";
        return "";
    }

    private static void AppendEnergyBudget(StringBuilder text, GrassInstance grass, EnvironmentData envData, GrassInstance.EnergyBudget budget)
    {
        int genes = grass.genome.GetDominantDNALocusCount();
        AppendFlow(text, '+', "Photosynthesis", budget.photosynthesis, budget.photosynthesisOn ? null : PhotosynthesisOffReason(grass, envData), null);
        AppendFlow(text, '+', "Respiration", budget.respiration, budget.respirationOn ? null : RespirationOffReason(grass, envData), "burns tile oxygen");
        AppendFlow(text, '-', "Gene upkeep", budget.geneUpkeep, null, genes + " genes");
        AppendFlow(text, '-', "Ageing", budget.ageing, null, null);
        AppendFlow(text, '-', "Growth", budget.growth, grass.IsDormant ? "dormant" : grass.grassDensity >= 1f ? "full size" : null, null);
        if (grass.IsSeeding)
        {
            AppendFlow(text, '-', "Seed provisioning", budget.seedProvisioning, grass.IsDormant ? "dormant" : null, null);
        }
        text.Append("  = <b>Net ").Append(FormatSigned(budget.Net)).Append("/s</b>\n");

        if (grass.nightEffect < 0.999f)
        {
            text.Append("  ").Append(Colored(MUTED_COLOR, "Night: upkeep, ageing and growth run at x" + grass.nightEffect.ToString("F2"))).Append('\n');
        }
    }

    private static void AppendFlow(StringBuilder text, char sign, string label, float value, string offReason, string detail)
    {
        text.Append("  ").Append(sign).Append(' ').Append(label).Append(": ");
        if (offReason != null)
        {
            text.Append(Colored(MUTED_COLOR, "off (" + offReason + ")")).Append('\n');
            return;
        }
        text.Append(sign).Append(Mathf.Abs(value).ToString("F2"));
        if (detail != null)
        {
            text.Append(' ').Append(Colored(MUTED_COLOR, "(" + detail + ")"));
        }
        text.Append('\n');
    }

    private static string PhotosynthesisOffReason(GrassInstance grass, EnvironmentData envData)
    {
        if (grass.IsDormant) return "dormant";
        if (grass.energyStored >= 100f) return "energy full";
        if (envData.light <= LIGHT_ACTIVITY_THRESHOLD) return "dark";
        return "inactive";
    }

    private static string RespirationOffReason(GrassInstance grass, EnvironmentData envData)
    {
        if (grass.IsDormant) return "dormant";
        if (grass.energyStored >= 100f) return "energy full";
        if (envData.light > LIGHT_ACTIVITY_THRESHOLD) return "daytime; night only";
        return "only below " + grass.genome.GetTraitValue(TraitId.RespirationTrigger).ToString("F0") + " energy";
    }

    private static void AppendReproduction(StringBuilder text, GrassInstance grass, GrassManager grassManager, TimeController timeController)
    {
        PlantGenome genome = grass.genome;

        // Cloning (GrassInstance.CheckAndHandleSpread + GrassManager clone limit)
        float cloneEnergy = genome.GetTraitValue(TraitId.ReproductionEnergyThreshold);
        int cloneLimit = grassManager != null ? grassManager.maxCloneAttemptsPerDay : 0;
        string cloneStatus;
        if (cloneEnergy < 0.0001f)
            cloneStatus = Colored(MUTED_COLOR, "no cloning gene");
        else if (grass.IsDormant)
            cloneStatus = Colored(WARN_COLOR, "paused while dormant");
        else if (grass.grassDensity < 1f)
            cloneStatus = Colored(WARN_COLOR, "needs full size (now " + (grass.grassDensity * 100f).ToString("F0") + "%)");
        else if (grass.energyStored < cloneEnergy)
            cloneStatus = Colored(WARN_COLOR, "needs " + cloneEnergy.ToString("F0") + " energy (has " + grass.energyStored.ToString("F0") + ")");
        else if (cloneLimit > 0 && grass.lastCloneDay == TimeController.simDaysSinceStart && grass.cloneAttemptsToday >= cloneLimit)
            cloneStatus = Colored(WARN_COLOR, "daily limit reached");
        else
            cloneStatus = Colored(GOOD_COLOR, "ready");
        text.Append("  Cloning: ").Append(cloneStatus).Append('\n');
        if (cloneEnergy >= 0.0001f)
        {
            text.Append("    ").Append(Colored(MUTED_COLOR, "Copies itself to a random free neighbour, giving the clone "
                + genome.GetTraitValue(TraitId.SpreadCost).ToString("F0") + " energy")).Append('\n');
        }

        // Flowering (GrassInstance.CheckFlowering + GrassManager.Flower monthly limit)
        float flowerEnergy = genome.GetTraitValue(TraitId.FloweringEnergyThreshold);
        float maturity = genome.GetTraitValue(TraitId.MaturityThreshold);
        int flowerLimit = grassManager != null ? grassManager.flowerLimit : 1;
        int daysPerMonth = grassManager != null && grassManager.seasonManager != null ? Mathf.Max(1, grassManager.seasonManager.DAYS_PER_MONTH) : 30;
        long flowersUsed = grass.lastFlowerMonth == TimeController.simDaysSinceStart / daysPerMonth ? grass.flowersThisMonth : 0;
        string flowerStatus;
        if (flowerEnergy < 0.0001f)
            flowerStatus = Colored(MUTED_COLOR, "no flowering gene");
        else if (grass.IsFlowering)
            flowerStatus = Colored(GOOD_COLOR, "flowering (day " + grass.floweringDays + ")");
        else if (grass.IsSeeding)
            flowerStatus = Colored(GOOD_COLOR, "done; seeds developing");
        else if (grass.IsDormant)
            flowerStatus = Colored(WARN_COLOR, "paused while dormant");
        else if (grass.grassDensity < maturity)
            flowerStatus = Colored(WARN_COLOR, "needs size " + (maturity * 100f).ToString("F0") + "% (now " + (grass.grassDensity * 100f).ToString("F0") + "%)");
        else if (grass.energyStored < flowerEnergy)
            flowerStatus = Colored(WARN_COLOR, "needs " + flowerEnergy.ToString("F0") + " energy (has " + grass.energyStored.ToString("F0") + ")");
        else if (flowersUsed >= flowerLimit)
            flowerStatus = Colored(WARN_COLOR, "monthly limit reached (" + flowersUsed + "/" + flowerLimit + ")");
        else
            flowerStatus = Colored(GOOD_COLOR, "ready");
        text.Append("  Flowering: ").Append(flowerStatus).Append('\n');
        if (grass.IsFlowering)
        {
            float selfRoll = Mathf.Clamp01((grass.floweringDays + 1) * 0.2f) * 100f;
            text.Append("    ").Append(Colored(MUTED_COLOR, "Pollen reaches " + genome.GetTraitValue(TraitId.PollenRange).ToString("F0")
                + " tiles daily. Tomorrow's self-pollination roll: " + selfRoll.ToString("F0") + "% (half set seed, half end flowering)")).Append('\n');
        }

        // Seeds (GrassManager.DisperseSeed, SeedManager germination)
        if (flowerEnergy < 0.0001f)
            return;

        float seedTarget = genome.GetTraitValue(TraitId.SeedEnergyThreshold);
        int seedCount = Mathf.Max(1, Mathf.RoundToInt(genome.GetTraitValue(TraitId.SeedCount)));
        int seedRange = Mathf.Max(1, (int)genome.GetTraitValue(TraitId.SeedDispersalRange));
        float energyPerSeed = seedTarget / seedCount;

        if (grass.IsSeeding && seedTarget > 0.0001f)
        {
            text.Append("  Seeds: ").Append(CreateBar(grass.seedingEnergy / seedTarget, 12)).Append(' ')
                .Append(grass.seedingEnergy.ToString("F0")).Append(" / ").Append(seedTarget.ToString("F0")).Append(" energy stored\n");
        }
        else
        {
            text.Append("  Seeds: ").Append(Colored(MUTED_COLOR, "none developing")).Append('\n');
        }
        text.Append("    ").Append(Colored(MUTED_COLOR, "Batch of " + seedCount + " seeds, " + energyPerSeed.ToString("F1")
            + " energy each, scattered up to " + seedRange + " tiles")).Append('\n');

        float germinationEnergy = genome.GetTraitValue(TraitId.GerminationEnergy);
        int stages = (byte)genome.GetTraitValue(TraitId.SproutStage);
        int stepsPerDay = timeController != null ? Mathf.Max(1, timeController.germinationStepsPerDay) : 24;
        float daysToSprout = stages / (float)stepsPerDay;
        float energyNeeded = germinationEnergy * stages + Mathf.Floor(daysToSprout); // stages plus ~1 energy of decay per night
        float sproutChance = Mathf.Clamp01(germinationEnergy * stages * 0.05f) * 100f;
        string viability = energyPerSeed >= energyNeeded ? Colored(GOOD_COLOR, "viable") : Colored(BAD_COLOR, "too small to sprout");
        text.Append("    Each seed needs ~").Append(energyNeeded.ToString("F1")).Append(" energy over ~").Append(daysToSprout.ToString("F1"))
            .Append(" days; sprout chance ").Append(sproutChance.ToString("F0")).Append("%: ").Append(viability).Append('\n');

        float cue = genome.GetTraitValue(TraitId.GerminationCue);
        float cold = genome.GetTraitValue(TraitId.SeedColdPreservation);
        text.Append("    ").Append(Colored(MUTED_COLOR,
            (cue > 0f ? "Seeds wait for habitat fit >= " + (cue * 100f).ToString("F0") + "%" : "Seeds sprout whatever the conditions")
            + "; " + (cold > 0f ? (cold * 100f).ToString("F0") + "% less decay in the cold" : "no cold protection"))).Append('\n');
    }

    private static string FormatSimDuration(float simSeconds)
    {
        float hourLength = Mathf.Max(0.0001f, TimeController.fullDayLength / 24f);
        float hours = simSeconds / hourLength;
        if (float.IsNaN(hours) || float.IsInfinity(hours) || hours > 24f * 999f)
            return "a very long time";
        if (hours < 1f)
            return "< 1 h";
        if (hours < 24f)
            return "~" + hours.ToString("F0") + " h";
        return "~" + (hours / 24f).ToString("F1") + " days";
    }

    private static string Colored(string color, string content)
    {
        return "<color=" + color + ">" + content + "</color>";
    }

    private static string GetDensityStage(float density)
    {
        if (density < 0.3f)
            return "Young";
        if (density < 0.7f)
            return "Developing";
        if (density < 0.99f)
            return "Mature";
        return "Full";
    }

    private static string FormatScore(float score)
    {
        float clamped = Mathf.Clamp01(score);
        float percent = clamped * 100f;
        string color = percent >= 75f ? GOOD_COLOR : percent >= POOR_FIT * 100f ? WARN_COLOR : BAD_COLOR;
        return Colored(color, percent.ToString("F0") + "%");
    }

    private static string FormatSigned(float value)
    {
        return (value >= 0f ? "+" : string.Empty) + value.ToString("F2");
    }

    private static string FormatTemperatureCelsius(float valueCelsius)
    {
        return valueCelsius.ToString("F1") + " C";
    }

    private static string FormatPercent(float value)
    {
        float clamped = Mathf.Clamp(value, 0f, 100f);
        return clamped.ToString("F1") + "%";
    }

    private static float ToDisplayLux(float simLight)
    {
        float nonNegative = Mathf.Max(0f, simLight);
        // Simulation stores light as a normalized factor; convert to estimated illuminance.
        return nonNegative <= 2f ? nonNegative * DISPLAY_LUX_PER_LIGHT_UNIT : nonNegative;
    }

    private static string FormatLux(float simLight)
    {
        float lux = ToDisplayLux(simLight);
        return lux >= 1000f ? lux.ToString("N0") + " lx" : lux.ToString("F1") + " lx";
    }

    private static float ToDisplayRadiationRate(float simRadiation)
    {
        float nonNegative = Mathf.Max(0f, simRadiation);
        // Simulation radiation is normalized/abstract; scale to a realistic environmental absorbed dose-rate estimate.
        return nonNegative <= 5f ? nonNegative * DISPLAY_USVH_PER_RADIATION_UNIT : nonNegative;
    }

    private static string FormatRadiationRate(float simRadiation)
    {
        return ToDisplayRadiationRate(simRadiation).ToString("F3") + " uSv/h";
    }

    private static string CreateBar(float value, int width = 15)
    {
        value = Mathf.Clamp01(value);
        int filled = Mathf.Clamp(Mathf.RoundToInt(value * width), 0, width);

        string color;
        char fillChar;
        if (value >= 0.75f)
        {
            color = "#00DD66";
            fillChar = '>';
        }
        else if (value >= 0.5f)
        {
            color = "#FFCC33";
            fillChar = '=';
        }
        else
        {
            color = "#FF6666";
            fillChar = '|';
        }

        string filledPart = filled > 0 ? new string(fillChar, filled) : string.Empty;
        return "[<color=" + color + ">" + filledPart + "</color>" + new string(' ', width - filled) + "]";
    }

    private void SetButtonsActive(bool active)
    {
        if (tabButtons != null)
        {
            for (int i = 0; i < tabButtons.Length; i++)
            {
                if (tabButtons[i] != null)
                {
                    tabButtons[i].SetActive(active);
                }
            }
        }
        if (quitButton != null)
        {
            quitButton.SetActive(active);
        }
    }
}

