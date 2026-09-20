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
        toggleButtons[0].onClick.AddListener(() => ToggleVisualization("Temperature"));
        toggleButtons[1].onClick.AddListener(() => ToggleVisualization("Hydration"));
        toggleButtons[2].onClick.AddListener(() => ToggleVisualization("Light"));
        toggleButtons[3].onClick.AddListener(() => ToggleVisualization("Radiation"));
        toggleButtons[4].onClick.AddListener(() => ToggleVisualization("Oxygen"));
        toggleButtons[5].onClick.AddListener(() => ToggleVisualization("Genus"));
    }

    void ToggleVisualization(string factor)
    {
        temperatureSliderGroup.SetActive(false);
        hydrationSliderGroup.SetActive(false);
        lightSliderGroup.SetActive(false);
        radiationSliderGroup.SetActive(false);

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
                    temperatureSliderGroup.SetActive(true);
                    break;
                case "Hydration":
                    hydrationSliderGroup.SetActive(true);
                    break;
                case "Light":
                    lightSliderGroup.SetActive(true);
                    break;
                case "Radiation":
                    radiationSliderGroup.SetActive(true);
                    break;
            }
        }
    }

    IEnumerator ResetTilesCoroutine()
    {
        yield return new WaitForEndOfFrame();
        mapManager.ResetOverlay();
    }

    public Color VisualizeFactor(EnvironmentData data)
    {
        Color tileColor = Color.white;
        switch (activeVisualization)
        {
            case "Temperature":
                float tempNormalized = Mathf.InverseLerp(
                    mapManager.environmentManager.minTemperature,
                    mapManager.environmentManager.maxTemperature,
                    data.temperature
                );
                tileColor = Color.Lerp(temperatureGradient[0], temperatureGradient[1], tempNormalized);
                break;
            case "Hydration":
                tileColor = Color.Lerp(hydrationGradient[0], hydrationGradient[1], data.hydration / 100f);
                break;

            case "Light":
                tileColor = Color.Lerp(lightGradient[0], lightGradient[1], data.light);
                break;

            case "Radiation":
                tileColor = Color.Lerp(radiationGradient[0], radiationGradient[1], data.radiation);
                break;

            case "Oxygen":
                tileColor = Color.Lerp(oxygenGradient[0], oxygenGradient[1], data.oxygen / 100f);
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
}