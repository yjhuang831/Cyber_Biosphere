using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class PlantGenome
{
    // Dense trait storage indexed by trait id; blank/unused traits remain 0f.
    public float[] phenotypes;

    [Header("Classification")]
    public string genus;
    public string species; // Backward-compatible taxonomy field; no longer used as a runtime restriction
    public string origin; // Human-visible provenance/origin tag for saved species and descendants
    // Library species this lineage descends from (32-hex GUID). Inherited by clones and by crosses within
    // the same lineage; null for wild plants and cross-lineage hybrids. Names are labels, this is the identity.
    public string speciesGuid;
    public Color genusColor; // Color assigned to this genus
    public int generation; // 0 for founders, increments by 1 per offspring event
    public List<Allele> maternalDNA;
    public List<Allele> paternalDNA;
    // 0 = maternal strand is dominant, 1 = paternal strand is dominant
    internal byte dominantDNA = 0;

    /// <summary>
    /// Classifies the plant type based on hydration requirement.
    /// Used for visual differentiation in the grass rendering system.
    /// </summary>
    public string GetPlantType()
    {
        float hydrationRequirement = GetTraitValue(TraitId.HydrationRequirement);

        if (hydrationRequirement < 30f)
            return "dryTolerant";
        else if (hydrationRequirement > 70f)
            return "wetTolerant";
        else
            return "balanced";
    }

    /// <summary>
    /// Safe accessor for trait values (returns 0f when trait is missing/blank).
    /// </summary>
    internal float GetTraitValue(int traitId)
    {
        if (phenotypes != null && traitId >= 0 && traitId < phenotypes.Length)
        {
            return phenotypes[traitId];
        }
        return 0f;
    }

    internal bool UsesMaternalDominantDNA()
    {
        return dominantDNA == 0;
    }

    internal List<Allele> GetDominantDNA()
    {
        bool useMaternal = UsesMaternalDominantDNA();
        if (useMaternal)
        {
            return maternalDNA;
        }
        return paternalDNA;
    }

    internal int GetDominantDNALocusCount()
    {
        List<Allele> dominant = GetDominantDNA();
        return dominant != null ? dominant.Count : 0;
    }

    internal void SetDominantDNA(bool useMaternal)
    {
        dominantDNA = useMaternal ? (byte)0 : (byte)1;
    }

    /// <summary>
    /// Independent copy (DNA lists and phenotypes included). Genomes are normally shared by reference,
    /// e.g. between seeds from one dispersal and the plants they sprout into.
    /// </summary>
    internal PlantGenome DeepCopy()
    {
        PlantGenome copy = (PlantGenome)MemberwiseClone();
        copy.phenotypes = phenotypes != null ? (float[])phenotypes.Clone() : null;
        copy.maternalDNA = maternalDNA != null ? new List<Allele>(maternalDNA) : null;
        copy.paternalDNA = paternalDNA != null ? new List<Allele>(paternalDNA) : null;
        return copy;
    }

    internal static string CombineOriginTags(string left, string right)
    {
        string primary = string.IsNullOrWhiteSpace(left) ? null : left.Trim();
        string secondary = string.IsNullOrWhiteSpace(right) ? null : right.Trim();

        if (string.IsNullOrEmpty(primary) && string.IsNullOrEmpty(secondary))
            return string.Empty;
        if (string.IsNullOrEmpty(primary))
            return secondary;
        if (string.IsNullOrEmpty(secondary))
            return primary;
        if (string.Equals(primary, secondary, System.StringComparison.OrdinalIgnoreCase))
            return primary;

        return primary + " × " + secondary;
    }

    int RandomSign()
    {
        return Random.value < 0.5f ? -1 : 1;
    }
}
