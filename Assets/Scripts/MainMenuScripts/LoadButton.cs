using UnityEngine;

public class LoadButton : MonoBehaviour
{
    internal string fileName;

    public void OnClick()
    {
        SharedUIPanelManager sharedUI = SharedUIPanelManager.Instance;
        if (sharedUI != null)
        {
            sharedUI.SelectSave(fileName);
            return;
        }

        Debug.LogWarning("Save selection skipped: SharedUIPanelManager was not found.");
    }

    public void DeleteSave()
    {
        SharedUIPanelManager sharedUI = SharedUIPanelManager.Instance;
        if (sharedUI != null)
        {
            sharedUI.DeleteSave(fileName);
            return;
        }

        Debug.LogWarning("Delete save skipped: SharedUIPanelManager was not found.");
    }
}
