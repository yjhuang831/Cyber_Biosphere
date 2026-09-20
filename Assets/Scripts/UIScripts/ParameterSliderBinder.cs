using UnityEngine;
using UnityEngine.UI;

public class ParameterSliderBinder : MonoBehaviour
{
    public EnvironmentManager manager;
    public string parameterName;
    public Slider slider;
    public Text valueText;

    void Start()
    {
        slider.onValueChanged.AddListener(OnSliderChanged);
        slider.value = manager.GetParameterValue(parameterName);
        UpdateValueText();
    }

    void OnSliderChanged(float value)
    {
        manager.ParameterSlider(parameterName, value);
        UpdateValueText();
    }

    void UpdateValueText()
    {
        float value = manager.GetParameterValue(parameterName);
        valueText.text = value.ToString("F1");
    }
}
