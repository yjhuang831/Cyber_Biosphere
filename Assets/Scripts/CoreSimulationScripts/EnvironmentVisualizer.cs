using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UI;
using System.Collections;

public class EnvironmentVisualizer : MonoBehaviour
{
    public Button[] toggleButtons;
    public Color[] temperatureGradient;
    public Color[] hydrationGradient;
    public Color[] lightGradient;
    public Color[] radiationGradient;
    public Color[] oxygenGradient;
    [Tooltip("Low -> high tile carbon (soil organic + dissolved + litter + plant biomass).")]
    public Color[] carbonGradient = { new Color(0.95f, 0.93f, 0.80f), new Color(0.30f, 0.18f, 0.05f) };
    [Tooltip("Tile carbon shown at full gradient strength. Fixed (not per-frame max) so colours stay comparable over time.")]
    [Min(0.01f)] public float carbonOverlayMax = 100f;

    public GameObject temperatureSliderGroup;
    public GameObject hydrationSliderGroup;
    public GameObject radiationSliderGroup;
    public GameObject lightSliderGroup;

    public MapManager mapManager;
    public string activeVisualization = "";

    void Start()
    {
        if (mapManager == null || mapManager.environmentManager == null)
        {
            Debug.LogError("EnvironmentVisualizer requires MapManager and EnvironmentManager references.", this);
            enabled = false;
            return;
        }

        if (toggleButtons == null || toggleButtons.Length < 6)
        {
            Debug.LogError("EnvironmentVisualizer requires six toggle buttons in Temperature, Hydration, Light, Radiation, Oxygen, Genus order (optional 7th: Carbon).", this);
            enabled = false;
            return;
        }

        for (int i = 0; i < 6; i++)
        {
            if (toggleButtons[i] == null)
            {
                Debug.LogError("EnvironmentVisualizer toggle button " + i + " is not assigned.", this);
                enabled = false;
                return;
            }
        }

        toggleButtons[0].onClick.AddListener(() => ToggleVisualization("Temperature"));
        toggleButtons[1].onClick.AddListener(() => ToggleVisualization("Hydration"));
        toggleButtons[2].onClick.AddListener(() => ToggleVisualization("Light"));
        toggleButtons[3].onClick.AddListener(() => ToggleVisualization("Radiation"));
        toggleButtons[4].onClick.AddListener(() => ToggleVisualization("Oxygen"));
        toggleButtons[5].onClick.AddListener(() => ToggleVisualization("Genus"));

        // Optional 7th button; alternatively wire any button's OnClick to ToggleCarbonVisualization()
        if (toggleButtons.Length > 6 && toggleButtons[6] != null)
        {
            toggleButtons[6].onClick.AddListener(ToggleCarbonVisualization);
        }
    }

    /// <summary>
    /// Shows/hides the carbon overlay: each tile's soil organic, dissolved, litter and plant biomass carbon.
    /// Atmospheric carbon is a single map-wide pool, so it is shown in the tile info panel instead.
    /// </summary>
    public void ToggleCarbonVisualization()
    {
        ToggleVisualization("Carbon");
    }

    void ToggleVisualization(string factor)
    {
        SetActiveIfAssigned(temperatureSliderGroup, false);
        SetActiveIfAssigned(hydrationSliderGroup, false);
        SetActiveIfAssigned(lightSliderGroup, false);
        SetActiveIfAssigned(radiationSliderGroup, false);

        if (activeVisualization == factor)
        {
            StartCoroutine(ResetTilesCoroutine());
            activeVisualization = "";
        }
        else
        {
            activeVisualization = factor;

            switch (activeVisualization)
            {
                case "Temperature":
                    SetActiveIfAssigned(temperatureSliderGroup, true);
                    break;
                case "Hydration":
                    SetActiveIfAssigned(hydrationSliderGroup, true);
                    break;
                case "Light":
                    SetActiveIfAssigned(lightSliderGroup, true);
                    break;
                case "Radiation":
                    SetActiveIfAssigned(radiationSliderGroup, true);
                    break;
            }
        }
    }

    IEnumerator ResetTilesCoroutine()
    {
        yield return new WaitForEndOfFrame();
        if (mapManager != null)
            mapManager.ResetOverlay();
    }

    private static void SetActiveIfAssigned(GameObject target, bool active)
    {
        if (target != null)
            target.SetActive(active);
    }

    public Color VisualizeFactor(EnvironmentData data)
    {
        if (data == null)
            return Color.clear;

        Color tileColor = Color.white;
        switch (activeVisualization)
        {
            case "Temperature":
                float tempNormalized = Mathf.InverseLerp(
                    mapManager.environmentManager.minTemperature,
                    mapManager.environmentManager.maxTemperature,
                    data.temperature
                );
                tileColor = BlendGradient(temperatureGradient, tempNormalized);
                break;
            case "Hydration":
                tileColor = BlendGradient(hydrationGradient, data.hydration / 100f);
                break;

            case "Light":
                tileColor = BlendGradient(lightGradient, data.light);
                break;

            case "Radiation":
                tileColor = BlendGradient(radiationGradient, data.radiation);
                break;

            case "Oxygen":
                tileColor = BlendGradient(oxygenGradient, data.oxygen / 100f);
                break;
            case "Carbon":
                double tileCarbon = data.soilOrganicCarbon + data.dissolvedCarbon + data.litterCarbon
                    + (data.grass != null ? data.grass.carbon : 0f);
                tileColor = BlendGradient(carbonGradient, (float)(tileCarbon / carbonOverlayMax));
                break;
            case "Genus":
                if (data.grass != null) {
                    tileColor = data.grass.genome.genusColor;
                }
                else
                {
                    tileColor = Color.clear;
                }
                break;
        }

        tileColor.a = 0.8f;
        return tileColor;
    }

    private static Color BlendGradient(Color[] gradient, float value)
    {
        if (gradient == null || gradient.Length < 2)
            return Color.white;

        return Color.Lerp(gradient[0], gradient[1], Mathf.Clamp01(value));
    }
}
