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
    private const float LIGHT_ACTIVITY_THRESHOLD = 0.01f;
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
            bool success = GenusManager.Instance.SaveLocalSpeciesGenome(currentData.grass.genome, genus, species);
            if (success)
            {
                Debug.Log("Species saved: " + genus + " " + species);
            }
        }

        CloseSaveSpeciesPanel();

        if (GenusManager.Instance != null)
        {
            GenusManager.Instance.LoadLocalSpeciesReferences();
        }
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
                " | Stage: " + seed.germinationStage + "/" + seed.sproutStage;
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
            grassInfoText.text = "No grass.";
            if (saveSpeciesButton != null)
                saveSpeciesButton.gameObject.SetActive(false);
            return;
        }

        // Show save button when there's grass
        if (saveSpeciesButton != null)
            saveSpeciesButton.gameObject.SetActive(true);

        string header = FormatOriginHeader(grass.genome.origin, grass.genome.genus, grass.genome.species);

        grassInfoText.text = header + "\n\n" + BuildPhenotypeText(grass.genome, currentData);
    }

    private static string FormatOriginHeader(string originRaw, string genusRaw, string speciesRaw)
    {
        string origin = string.IsNullOrWhiteSpace(originRaw)
            ? (string.IsNullOrWhiteSpace(genusRaw) || string.IsNullOrWhiteSpace(speciesRaw)
                ? "Unknown origin"
                : (genusRaw.Trim() + " " + speciesRaw.Trim()))
            : originRaw.Trim();

        if (string.IsNullOrWhiteSpace(origin))
        {
            origin = "Unknown origin";
        }

        return "<b><size=115%>" + origin + "</size></b>";
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

    private static string BuildPhenotypeText(PlantGenome genome, EnvironmentData envData = null)
    {
        if (genome == null || genome.phenotypes == null || genome.phenotypes.Length == 0)
            return "Phenotypes: none.";

        StringBuilder text = new StringBuilder(640);

        text.Append("<b>[STATUS]</b>\n");
        if (envData != null && envData.grass != null)
        {
            AppendPlantStatus(text, envData.grass, genome);
        }
        else
        {
            text.Append("  No live grass state data.\n");
        }

        text.Append("\n<b>[ENVIRONMENT FIT]</b>\n");
        AppendEnvironmentFit(text, genome, envData);

        text.Append("\n<b>[ENERGY FLOW]</b>\n");
        AppendEnergyFlow(text, genome, envData);

        text.Append("\n<b>[REPRODUCTION]</b>\n");
        AppendReproduction(text, genome, envData);

        return text.ToString().TrimEnd();
    }

    private static void AppendPlantStatus(StringBuilder text, GrassInstance grass, PlantGenome genome)
    {
        text.Append("  State: ").Append(GetStateLabel(grass)).Append('\n');
        text.Append("  Generation: ").Append(Mathf.Max(0, genome.generation)).Append('\n');
        text.Append("  Age: ").Append(grass.age).Append(" days");
        text.Append(" | Aging load ").Append(CalculateAgingLoadPerSecond(grass, genome).ToString("F2")).Append(" J/s");
        text.Append('\n');

        text.Append("  Energy: ")
            .Append(CreateBar(grass.energyStored / 100f, 12))
            .Append(' ')
            .Append(grass.energyStored.ToString("F1"))
            .Append(" / 100 kJ\n");

        text.Append("  Density: ")
            .Append(CreateBar(grass.grassDensity, 12))
            .Append(' ')
            .Append((grass.grassDensity * 100f).ToString("F1"))
            .Append("% (")
            .Append(GetDensityStage(grass.grassDensity))
            .Append(")\n");

        text.Append("  Type: ").Append(genome.GetPlantType()).Append('\n');
    }

    private static float CalculateAgingLoadPerSecond(GrassInstance grass, PlantGenome genome)
    {
        if (grass == null || genome == null)
            return 0f;

        float nightEfficiency = genome.GetTraitValue(TraitId.NightGrowthEfficiency);
        float nightFactor = (TimeController.isNight && nightEfficiency > 0.0001f) ? nightEfficiency : 1f;
        float agingLoadPerSecond = grass.ageEffect * grass.dormancyEffect * nightFactor * grass.grassDensity;
        return Mathf.Max(0f, agingLoadPerSecond);
    }

    private static void AppendEnvironmentFit(StringBuilder text, PlantGenome genome, EnvironmentData envData)
    {
        float lightReq = Mathf.Max(0.0001f, genome.GetTraitValue(TraitId.LightRequirement));
        float hydrationReq = Mathf.Max(0.0001f, genome.GetTraitValue(TraitId.HydrationRequirement));
        float tempOpt = genome.GetTraitValue(TraitId.TemperatureTolerance);

        if (envData == null || envData.grass == null)
        {
            text.Append("  Light req: ").Append(FormatLux(lightReq)).Append('\n');
            text.Append("  Water req: ").Append(FormatPercent(hydrationReq)).Append('\n');
            text.Append("  Temp opt: ").Append(FormatTemperatureCelsius(tempOpt)).Append('\n');
            return;
        }

        EnergyDiagnostics diagnostics = CalculateEnergyDiagnostics(genome, envData);
        float lightScore = Mathf.Clamp01(diagnostics.lightEfficiency);
        float hydrationScore = Mathf.Clamp01(diagnostics.hydrationEfficiency);
        float tempScore = Mathf.Clamp01(diagnostics.temperatureMultiplier);
        float habitatScore = (lightScore + hydrationScore + tempScore) / 3f;

        string limitingFactor = "Light";
        float limitingScore = lightScore;
        if (hydrationScore < limitingScore)
        {
            limitingScore = hydrationScore;
            limitingFactor = "Water";
        }
        if (tempScore < limitingScore)
        {
            limitingFactor = "Temperature";
            limitingScore = tempScore;
        }

        text.Append("  Habitat score: ").Append(FormatScore(habitatScore))
            .Append(" | Limiting: ").Append(limitingFactor).Append(' ').Append(FormatScore(limitingScore)).Append('\n');
        text.Append("  Light: ").Append(FormatLux(envData.light)).Append(" / ").Append(FormatLux(lightReq))
            .Append(" (").Append(DescribeRelative(envData.light, lightReq, "low", "excess")).Append(", ")
            .Append(FormatScore(lightScore)).Append(")\n");
        text.Append("  Water: ").Append(FormatPercent(envData.hydration)).Append(" / ").Append(FormatPercent(hydrationReq))
            .Append(" (").Append(DescribeRelative(envData.hydration, hydrationReq, "dry", "wet")).Append(", ")
            .Append(FormatScore(hydrationScore)).Append(")\n");
        text.Append("  Temp: ").Append(FormatTemperatureCelsius(envData.temperature)).Append(" / ").Append(FormatTemperatureCelsius(tempOpt))
            .Append(" (").Append(DescribeTemperature(envData.temperature, tempOpt)).Append(", ")
            .Append(FormatScore(tempScore)).Append(")\n");
        text.Append("  Radiation: ").Append(FormatRadiationRate(envData.radiation))
            .Append(" | Oxygen: ").Append(FormatPercent(envData.oxygen)).Append('\n');
    }

    private static void AppendEnergyFlow(StringBuilder text, PlantGenome genome, EnvironmentData envData)
    {
        float respirationRate = genome.GetTraitValue(TraitId.RespirationRate);
        float dormancyTrigger = genome.GetTraitValue(TraitId.DormancyTrigger);
        float nightEfficiency = genome.GetTraitValue(TraitId.NightGrowthEfficiency);

        if (envData == null || envData.grass == null)
        {
            text.Append("  Respiration rate: ").Append(respirationRate.ToString("F2")).Append('\n');
            text.Append("  Dormancy trigger: ").Append(dormancyTrigger.ToString("F2")).Append('\n');
            text.Append("  Night efficiency: ").Append(nightEfficiency.ToString("F2")).Append('\n');
            return;
        }

        GrassInstance grass = envData.grass;
        EnergyDiagnostics diagnostics = CalculateEnergyDiagnostics(genome, envData);
        float netPotential = diagnostics.netPotential;
        string mode = GetEnergyMode(grass, diagnostics);

        text.Append("  Mode: ").Append(mode).Append('\n');
        text.Append("  Daylight: ").Append(diagnostics.isDaylight ? "yes" : "no")
            .Append(" (").Append(FormatLux(envData.light)).Append(")\n");
        text.Append("  Respiration rule: dark only, and only below trigger.\n");
        text.Append("  Photosynthesis (active): ").Append(FormatSigned(diagnostics.activePhotosynthesis)).Append(" J/s");
        if (!diagnostics.photosynthesisActive)
            text.Append(" (off)");
        text.Append('\n');
        text.Append("  Respiration (active): -").Append(diagnostics.activeRespiration.ToString("F2")).Append(" J/s");
        if (!diagnostics.respirationActive)
            text.Append(" (off)");
        text.Append('\n');
        text.Append("  Respiration trigger: ").Append(diagnostics.respirationTrigger.ToString("F2")).Append(" kJ")
            .Append(" | Energy ").Append(grass.energyStored.ToString("F1")).Append(" kJ\n");
        text.Append("  Net active: ").Append(FormatSigned(netPotential)).Append(" J/s (").Append(FormatTrend(netPotential)).Append(")\n");
        text.Append("  Potential (reference): day +").Append(diagnostics.photosynthesisPotential.ToString("F2"))
            .Append(" J/s | night -").Append(diagnostics.respirationPotential.ToString("F2")).Append(" J/s\n");
        text.Append("  Efficiency: light ").Append(FormatScore(Mathf.Clamp01(diagnostics.lightEfficiency)))
            .Append(" | water ").Append(FormatScore(Mathf.Clamp01(diagnostics.hydrationEfficiency)))
            .Append(" | temp ").Append(FormatScore(Mathf.Clamp01(diagnostics.temperatureMultiplier))).Append('\n');

        if (dormancyTrigger > 0.0001f)
        {
            bool dormancyRisk = !grass.IsDormant && grass.grassDensity >= grass.maturityThreshold && diagnostics.basePotential < dormancyTrigger;
            text.Append("  Dormancy check: base ").Append(diagnostics.basePotential.ToString("F2"))
                .Append(" vs trigger ").Append(dormancyTrigger.ToString("F2"))
                .Append(" (").Append(dormancyRisk ? "<color=#FF6666>risk</color>" : "<color=#00DD66>safe</color>").Append(")\n");
        }
        else
        {
            text.Append("  Dormancy check: gene disabled\n");
        }
    }

    private static void AppendReproduction(StringBuilder text, PlantGenome genome, EnvironmentData envData)
    {
        float maturityThreshold = Mathf.Max(0.0001f, genome.GetTraitValue(TraitId.MaturityThreshold));
        float spreadThreshold = genome.GetTraitValue(TraitId.ReproductionEnergyThreshold);
        float floweringThreshold = genome.GetTraitValue(TraitId.FloweringEnergyThreshold);
        float seedEnergyThreshold = genome.GetTraitValue(TraitId.SeedEnergyThreshold);
        float seedCount = genome.GetTraitValue(TraitId.SeedCount);
        float pollenRange = genome.GetTraitValue(TraitId.PollenRange);
        float seedRange = genome.GetTraitValue(TraitId.SeedDispersalRange);

        bool spreadGeneEnabled = spreadThreshold > 0.0001f;
        bool floweringGeneEnabled = floweringThreshold > 0.0001f && seedCount > 0.5f;

        if (envData == null || envData.grass == null)
        {
            text.Append("  Spread gene: ").Append(spreadGeneEnabled ? "enabled" : "disabled").Append('\n');
            text.Append("  Flowering gene: ").Append(floweringGeneEnabled ? "enabled" : "disabled").Append('\n');
            text.Append("  Seeds/cycle: ").Append(seedCount.ToString("F0"))
                .Append(" | Pollen range: ").Append(pollenRange.ToString("F0"))
                .Append(" | Seed range: ").Append(seedRange.ToString("F0")).Append('\n');
            return;
        }

        GrassInstance grass = envData.grass;

        bool maturityReady = grass.grassDensity >= maturityThreshold;
        bool spreadReady = spreadGeneEnabled && grass.grassDensity >= 1f && grass.energyStored >= spreadThreshold;
        bool floweringReady = floweringGeneEnabled && maturityReady && grass.energyStored >= floweringThreshold;
        bool seedReady = floweringGeneEnabled && grass.IsSeeding && seedEnergyThreshold > 0.0001f && grass.seedingEnergy >= seedEnergyThreshold;

        text.Append("  Maturity: ")
            .Append(CreateBar(grass.grassDensity / maturityThreshold, 12))
            .Append(' ')
            .Append((grass.grassDensity * 100f).ToString("F1"))
            .Append("% / ")
            .Append((maturityThreshold * 100f).ToString("F1"))
            .Append("%\n");

        text.Append("  Spread: ").Append(FormatReadiness(spreadGeneEnabled, spreadReady))
            .Append(" | E ").Append(grass.energyStored.ToString("F1")).Append(" / ").Append(FormatThreshold(spreadGeneEnabled, spreadThreshold))
            .Append(" | Density ").Append((grass.grassDensity * 100f).ToString("F1")).Append("%\n");

        text.Append("  Flowering: ").Append(FormatReadiness(floweringGeneEnabled, floweringReady))
            .Append(" | E ").Append(grass.energyStored.ToString("F1")).Append(" / ").Append(FormatThreshold(floweringGeneEnabled, floweringThreshold))
            .Append(" | Mature ").Append(maturityReady ? "yes" : "no").Append('\n');

        if (floweringGeneEnabled && seedEnergyThreshold > 0.0001f)
        {
            text.Append("  Seed build: ")
                .Append(CreateBar(grass.seedingEnergy / seedEnergyThreshold, 12))
                .Append(' ')
                .Append(grass.seedingEnergy.ToString("F1"))
                .Append(" / ")
                .Append(seedEnergyThreshold.ToString("F1"))
                .Append(" kJ")
                .Append(seedReady ? " (ready)" : grass.IsSeeding ? " (building)" : " (idle)")
                .Append('\n');
        }
        else
        {
            text.Append("  Seed build: not available\n");
        }

        text.Append("  Output: ").Append(seedCount.ToString("F0"))
            .Append(" seeds/cycle | Pollen ").Append(pollenRange.ToString("F0"))
            .Append(" | Seed range ").Append(seedRange.ToString("F0")).Append('\n');
        text.Append("  Next milestone: ").Append(GetNextMilestone(grass, spreadGeneEnabled, floweringGeneEnabled, maturityThreshold, spreadThreshold, floweringThreshold, seedEnergyThreshold)).Append('\n');
    }

    private static EnergyDiagnostics CalculateEnergyDiagnostics(PlantGenome genome, EnvironmentData envData)
    {
        EnergyDiagnostics diagnostics = new EnergyDiagnostics();
        if (genome == null || envData == null || envData.grass == null)
            return diagnostics;

        diagnostics.lightRequirement = Mathf.Max(0.0001f, genome.GetTraitValue(TraitId.LightRequirement));
        diagnostics.hydrationRequirement = Mathf.Max(0.0001f, genome.GetTraitValue(TraitId.HydrationRequirement));
        diagnostics.temperatureTolerance = genome.GetTraitValue(TraitId.TemperatureTolerance);
        diagnostics.respirationRate = genome.GetTraitValue(TraitId.RespirationRate);
        diagnostics.respirationTrigger = genome.GetTraitValue(TraitId.RespirationTrigger);

        diagnostics.lightEfficiency = Mathf.Clamp01(envData.light / diagnostics.lightRequirement);
        if (envData.light > diagnostics.lightRequirement + 0.1f)
        {
            diagnostics.lightEfficiency -= envData.light - diagnostics.lightRequirement - 0.1f;
        }

        diagnostics.hydrationEfficiency = Mathf.Clamp01(envData.hydration / diagnostics.hydrationRequirement);
        if (envData.hydration > diagnostics.hydrationRequirement)
        {
            diagnostics.hydrationEfficiency -= (envData.hydration - diagnostics.hydrationRequirement) / 80f;
        }

        float tempDifference = envData.temperature - diagnostics.temperatureTolerance;
        diagnostics.temperatureMultiplier = tempDifference >= 0f
            ? Mathf.Clamp01(1.5f - (tempDifference / 10f))
            : Mathf.Clamp01(1f + (tempDifference / 10f));

        diagnostics.basePotential = 2f * diagnostics.hydrationEfficiency * diagnostics.temperatureMultiplier;
        diagnostics.isDaylight = envData.light > LIGHT_ACTIVITY_THRESHOLD;
        diagnostics.photosynthesisPotential = diagnostics.basePotential * diagnostics.lightEfficiency * envData.grass.grassDensity;
        diagnostics.respirationPotential = diagnostics.respirationRate * envData.grass.grassDensity * envData.oxygen * 0.05f * envData.grass.dormancyEffect;

        diagnostics.photosynthesisActive = envData.grass.usedPhotosynthesisLastTick;
        diagnostics.respirationActive = envData.grass.usedRespirationLastTick;

        diagnostics.activePhotosynthesis = diagnostics.photosynthesisActive ? diagnostics.photosynthesisPotential : 0f;
        diagnostics.activeRespiration = diagnostics.respirationActive ? diagnostics.respirationPotential : 0f;
        diagnostics.netPotential = diagnostics.activePhotosynthesis - diagnostics.activeRespiration;
        return diagnostics;
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

    private static string GetStateLabel(GrassInstance grass)
    {
        if (grass.IsDormant)
            return "Dormant";
        if (grass.IsSeeding)
            return "Seeding";
        if (grass.IsFlowering)
            return "Flowering";
        return "Active";
    }

    private static string DescribeRelative(float current, float target, string lowWord, string highWord)
    {
        float tolerance = Mathf.Max(0.05f, Mathf.Abs(target) * 0.1f);
        float delta = current - target;
        if (Mathf.Abs(delta) <= tolerance)
            return "balanced";
        return delta < 0f ? lowWord : highWord;
    }

    private static string DescribeTemperature(float current, float optimal)
    {
        float delta = current - optimal;
        float absDelta = Mathf.Abs(delta);
        if (absDelta <= 2f)
            return "balanced";
        if (delta < 0f)
            return absDelta > 8f ? "cold stress" : "cool";
        return absDelta > 8f ? "heat stress" : "warm";
    }

    private static string FormatScore(float score)
    {
        float clamped = Mathf.Clamp01(score);
        float percent = clamped * 100f;
        string color = percent >= 75f ? "#00DD66" : percent >= 45f ? "#FFCC33" : "#FF6666";
        return "<color=" + color + ">" + percent.ToString("F0") + "%</color>";
    }

    private static string FormatSigned(float value)
    {
        return (value >= 0f ? "+" : string.Empty) + value.ToString("F2");
    }

    private static string FormatTrend(float netPotential)
    {
        if (netPotential > 0.02f)
            return "<color=#00DD66>charging</color>";
        if (netPotential < -0.02f)
            return "<color=#FF6666>draining</color>";
        return "<color=#FFCC33>stable</color>";
    }

    private static string GetEnergyMode(GrassInstance grass, EnergyDiagnostics diagnostics)
    {
        if (grass.IsDormant)
            return "Dormant recovery";
        if (grass.energyStored >= 100f)
            return "Energy cap reached";
        if (diagnostics.photosynthesisActive)
            return "Photosynthesis active";
        if (diagnostics.respirationActive)
            return "Respiration fallback (night)";

        return diagnostics.isDaylight ? "Day idle" : "Night idle";
    }

    private static string FormatReadiness(bool enabled, bool ready)
    {
        if (!enabled)
            return "<color=#999999>disabled</color>";
        return ready ? "<color=#00DD66>ready</color>" : "<color=#FFAA33>not ready</color>";
    }

    private static string FormatThreshold(bool enabled, float threshold)
    {
        if (!enabled)
            return "n/a";
        return threshold.ToString("F1") + " kJ";
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

    private static string GetNextMilestone(
        GrassInstance grass,
        bool spreadGeneEnabled,
        bool floweringGeneEnabled,
        float maturityThreshold,
        float spreadThreshold,
        float floweringThreshold,
        float seedEnergyThreshold)
    {
        if (!spreadGeneEnabled && !floweringGeneEnabled)
            return "No reproduction genes enabled.";
        if (grass.grassDensity < maturityThreshold)
            return "Grow density to maturity threshold.";
        if (floweringGeneEnabled && grass.energyStored < floweringThreshold)
            return "Store energy for flowering.";
        if (spreadGeneEnabled && grass.grassDensity < 1f)
            return "Reach full density for spreading.";
        if (spreadGeneEnabled && grass.energyStored < spreadThreshold)
            return "Store energy for spreading.";
        if (grass.IsSeeding && seedEnergyThreshold > 0.0001f && grass.seedingEnergy < seedEnergyThreshold)
            return "Accumulate seed energy for dispersal.";
        return "Ready for next reproductive event.";
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

    private struct EnergyDiagnostics
    {
        public float lightRequirement;
        public float hydrationRequirement;
        public float temperatureTolerance;
        public float respirationRate;
        public float respirationTrigger;
        public float lightEfficiency;
        public float hydrationEfficiency;
        public float temperatureMultiplier;
        public float basePotential;
        public float photosynthesisPotential;
        public float respirationPotential;
        public float activePhotosynthesis;
        public float activeRespiration;
        public float netPotential;
        public bool isDaylight;
        public bool photosynthesisActive;
        public bool respirationActive;
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

