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
            Debug.LogError("EnvironmentVisualizer requires six toggle buttons in Temperature, Hydration, Light, Radiation, Oxygen, Genus order.", this);
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
