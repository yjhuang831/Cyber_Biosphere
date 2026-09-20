using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class SeasonManager : MonoBehaviour
{
    [Header("Seasonal Settings")]
    public int monthsPerTransition = 1;
    internal static int daysPerYear = 360;
    private int currentSeason = 0; // 0: Spring, 1: Summer, 2: Fall, 3: Winter

    [Header("Season UI")]
    public Button[] seasonButtons = new Button[4];
    public Color activeSeasonColor = Color.white;
    public Color inactiveSeasonColor = new Color(1f, 1f, 1f, 0.35f);
    public Slider seasonProgressSlider;
    private readonly List<Season> seasons = new List<Season>();

    [Header("References")]
    public EnvironmentManager environmentManager;
    public TimeController timeController;

    private Coroutine seasonTransitionRoutine;
    private bool isDragging = false;
    private int hoverSeason = -1;
    internal int DAYS_PER_MONTH = 30;

    void Awake()
    {
        seasons.Clear();
        seasons.Add(new Season("Spring", 0f, 13f));
        seasons.Add(new Season("Summer", 18f, 16f));
        seasons.Add(new Season("Fall", 3f, 12f));
        seasons.Add(new Season("Winter", -35f, 8f));

        ApplySeasonImmediate(0);
        daysPerYear = 12 * DAYS_PER_MONTH;
    }

    void Start()
    {
        RegisterSeasonButtons();
        UpdateSeasonButtonVisuals();
        ConfigureProgressSlider();
    }

    private void RegisterSeasonButtons()
    {
        if (seasonButtons == null || seasonButtons.Length == 0)
            return;

        for (int i = 0; i < seasonButtons.Length; i++)
        {
            int index = i;
            Button button = seasonButtons[i];
            if (button == null)
                continue;

            button.onClick.AddListener(() => OnSeasonButtonClicked(index));

            EventTrigger trigger = button.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = button.gameObject.AddComponent<EventTrigger>();
            }
            if (trigger.triggers == null)
            {
                trigger.triggers = new List<EventTrigger.Entry>();
            }

            AddEventTrigger(trigger, EventTriggerType.PointerDown, _ => OnSeasonPointerDown(index));
            AddEventTrigger(trigger, EventTriggerType.PointerEnter, _ => OnSeasonPointerEnter(index));
            AddEventTrigger(trigger, EventTriggerType.PointerUp, _ => OnSeasonPointerUp(index));
        }
    }

    private static void AddEventTrigger(EventTrigger trigger, EventTriggerType type, System.Action<BaseEventData> handler)
    {
        EventTrigger.Entry entry = new EventTrigger.Entry();
        entry.eventID = type;
        entry.callback.AddListener(data => handler(data));
        trigger.triggers.Add(entry);
    }

    private void OnSeasonButtonClicked(int seasonIndex)
    {
        if (seasonIndex == currentSeason)
            return;

        StopSeasonTransition();
        ApplySeasonImmediate(seasonIndex);
    }

    private void OnSeasonPointerDown(int seasonIndex)
    {
        if (seasonIndex != currentSeason)
            return;
        if (seasonTransitionRoutine != null)
            return;

        isDragging = true;
        hoverSeason = seasonIndex;
    }

    private void OnSeasonPointerEnter(int seasonIndex)
    {
        if (!isDragging)
            return;

        hoverSeason = seasonIndex;
    }

    private void OnSeasonPointerUp(int seasonIndex)
    {
        if (!isDragging)
            return;

        isDragging = false;
        int targetSeason = hoverSeason >= 0 ? hoverSeason : seasonIndex;
        hoverSeason = -1;

        if (targetSeason == currentSeason)
            return;

        StartSeasonTransition(targetSeason);
    }

    private void StartSeasonTransition(int targetSeason)
    {
        if (seasonTransitionRoutine != null)
            return;
        if (targetSeason < 0 || targetSeason >= seasons.Count)
            return;

        seasonTransitionRoutine = StartCoroutine(SeasonTransitionCoroutine(targetSeason));
    }

    private IEnumerator SeasonTransitionCoroutine(int targetSeason)
    {
        float startTemp = environmentManager != null ? environmentManager.seasonalTemperatureOffset : seasons[currentSeason].targetTemperature;
        float startNoon = timeController != null ? timeController.noonTime : seasons[currentSeason].targetNoonLength;
        float targetTemp = seasons[targetSeason].targetTemperature;
        float targetNoon = seasons[targetSeason].targetNoonLength;

        long lastDay = TimeController.simDaysSinceStart;
        int steps = Mathf.Max(1, monthsPerTransition * DAYS_PER_MONTH);
        SetProgressSlider(0f, true);

        for (int step = 1; step <= steps; step++)
        {
            while (TimeController.simDaysSinceStart == lastDay)
            {
                yield return null;
            }
            lastDay = TimeController.simDaysSinceStart;

            float t = step / (float)steps;
            float eased = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * t);
            ApplySeasonBlend(startTemp, targetTemp, startNoon, targetNoon, eased);
            SetProgressSlider(t, true);
        }

        ApplySeasonImmediate(targetSeason);
        SetProgressSlider(1f, false);
        seasonTransitionRoutine = null;
    }

    private void ApplySeasonImmediate(int seasonIndex)
    {
        if (seasonIndex < 0 || seasonIndex >= seasons.Count)
            return;

        currentSeason = seasonIndex;
        ApplySeasonBlend(seasons[seasonIndex].targetTemperature, seasons[seasonIndex].targetTemperature,
            seasons[seasonIndex].targetNoonLength, seasons[seasonIndex].targetNoonLength, 1f);

        UpdateSeasonButtonVisuals();
        Debug.Log("Season set to: " + seasons[seasonIndex].name);
    }

    private void ApplySeasonBlend(float startTemp, float targetTemp, float startNoon, float targetNoon, float t)
    {
        float temp = Mathf.Lerp(startTemp, targetTemp, t);
        float noon = Mathf.Lerp(startNoon, targetNoon, t);

        if (environmentManager != null)
        {
            // [OPTIMIZED] Skip hydration update during season transitions since map topology hasn't changed
            // Only temperature needs update based on new seasonal offset
            environmentManager.seasonalTemperatureOffset = temp;
            environmentManager.UpdateTileEnvironment(null, false, false, 0f, skipHydrationUpdate: true);
        }
        if (timeController != null)
        {
            timeController.noonTime = noon;
            timeController.UpdateLightDistribution();
        }
    }

    private void UpdateSeasonButtonVisuals()
    {
        if (seasonButtons == null || seasonButtons.Length == 0)
            return;

        for (int i = 0; i < seasonButtons.Length; i++)
        {
            Button button = seasonButtons[i];
            if (button == null)
                continue;

            Image img = button.GetComponent<Image>();
            if (img == null)
                continue;

            img.color = (i == currentSeason) ? activeSeasonColor : inactiveSeasonColor;
        }
    }

    private void StopSeasonTransition()
    {
        if (seasonTransitionRoutine != null)
        {
            StopCoroutine(seasonTransitionRoutine);
            seasonTransitionRoutine = null;
        }
        SetProgressSlider(0f, false);
    }

    public class Season
    {
        public string name;
        public float targetTemperature;
        public float targetNoonLength;
        public Season(string name, float targetTemp, float noon)
        {
            this.name = name;
            this.targetTemperature = targetTemp;
            this.targetNoonLength = noon;
        }
    }

    private void ConfigureProgressSlider()
    {
        if (seasonProgressSlider == null)
            return;

        seasonProgressSlider.minValue = 0f;
        seasonProgressSlider.maxValue = 1f;
        seasonProgressSlider.value = 0f;
        seasonProgressSlider.interactable = false;
        seasonProgressSlider.gameObject.SetActive(false);
    }

    private void SetProgressSlider(float value, bool visible)
    {
        if (seasonProgressSlider == null)
            return;

        if (visible && !seasonProgressSlider.gameObject.activeSelf)
        {
            seasonProgressSlider.gameObject.SetActive(true);
        }
        seasonProgressSlider.value = Mathf.Clamp01(value);
        if (!visible && seasonProgressSlider.gameObject.activeSelf)
        {
            seasonProgressSlider.gameObject.SetActive(false);
        }
    }
}
