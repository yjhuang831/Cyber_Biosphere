using UnityEngine;
using UnityEngine.UI;

public class GrassDataPanelController : MonoBehaviour
{
    private const float DISPLAY_LUX_PER_LIGHT_UNIT = 100000f;

    [Header("References")]
    public MapManager mapManager;
    public Canvas canvas;

    [Header("UI Elements")]
    public GameObject grassDataPanel;
    public Text idText;
    public Text energyText;
    public Text densityText;
    public Text tempText;
    public Text lightText;

    [Header("Layout")]
    [SerializeField] private Vector2 panelOffset = new Vector2(150f, 0f);

    void Start()
    {
        if (grassDataPanel != null)
        {
            grassDataPanel.SetActive(false);
        }
    }

    public void ShowGrassData(EnvironmentData data, Vector3Int gridPosition)
    {
        if (grassDataPanel == null || canvas == null || mapManager == null)
            return;
        if (data == null || data.grass == null)
            return;

        grassDataPanel.SetActive(true);

        Vector3 worldPos = mapManager.terrainTilemap.GetCellCenterWorld(gridPosition);
        Vector3 screenPos = Camera.main.WorldToScreenPoint(worldPos);

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvas.transform as RectTransform,
            screenPos,
            canvas.worldCamera,
            out Vector2 localPoint
        );

        grassDataPanel.GetComponent<RectTransform>().localPosition = localPoint + panelOffset;

        GrassInstance grass = data.grass;
        PlantGenome genome = grass.genome;

        string originText = string.IsNullOrWhiteSpace(genome.origin)
            ? ((genome.genus ?? "unknown") + " " + (genome.species ?? "unknown"))
            : genome.origin;
        idText.text = originText;
        energyText.text = "Energy: " + grass.energyStored.ToString("F1") + " kJ";
        densityText.text = "Density: " + (grass.grassDensity * 100f).ToString("F1") + "%";
        tempText.text = "Temp Opt: " + FormatTemperatureCelsius(genome.GetTraitValue(TraitId.TemperatureTolerance));
        lightText.text = "Light Req: " + FormatLux(genome.GetTraitValue(TraitId.LightRequirement));
    }

    public void Hide()
    {
        if (grassDataPanel != null)
        {
            grassDataPanel.SetActive(false);
        }
    }

    private static string FormatTemperatureCelsius(float valueCelsius)
    {
        return valueCelsius.ToString("F1") + " C";
    }

    private static float ToDisplayLux(float simLight)
    {
        float nonNegative = Mathf.Max(0f, simLight);
        return nonNegative <= 2f ? nonNegative * DISPLAY_LUX_PER_LIGHT_UNIT : nonNegative;
    }

    private static string FormatLux(float simLight)
    {
        float lux = ToDisplayLux(simLight);
        return lux >= 1000f ? lux.ToString("N0") + " lx" : lux.ToString("F1") + " lx";
    }
}
