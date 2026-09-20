using UnityEngine;

[CreateAssetMenu(menuName = "Genetics/TraitDefinition")]
public class TraitDefinition : ScriptableObject
{
    [Tooltip("Unique numeric id (optional). If unused, index in database is used at runtime.")]
    public int id = -1;

    [Tooltip("Human-readable trait name")]
    public string traitName = "NewTrait";

    [Tooltip("Min/max clamp for final phenotype")]
    public float minValue = 0f;
    public float maxValue = 100f;
    public float baseValue = -1f;

    [Tooltip("Suggested number of loci to allocate initially (min..max).")]
    public int minLoci = 0;
    public int maxLoci = 4;
}