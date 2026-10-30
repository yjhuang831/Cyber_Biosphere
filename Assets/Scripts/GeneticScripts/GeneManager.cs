using UnityEngine;
using System.Collections.Generic;
using Random = UnityEngine.Random;

/// <summary>
/// Genome creation, inheritance, mutation, and DNA-to-phenotype translation.
///
/// Strand invariant: maternal and paternal DNA are always sorted by traitId, so a locus is identified by its trait
/// and copy number. The nth copy of a trait on one strand is the homolog of the nth copy on the other, and code that
/// reads both strands walks them in lockstep (see CalculatePhenotypes, Meiosis). A copy with no partner is hemizygous:
/// it is expressed on its own, and passed on only when the gamete is reading its strand.
/// Every strand edit must preserve the sort order (see InsertLocus).
/// </summary>
public class GeneManager : MonoBehaviour
{
    private const int MAX_ALLELE_VALUE = 20;
    private const int MAX_DNA_LENGTH = 256;
    private const float CROSSOVER_RATE = 0.15f;   // Chance of switching strand at each homologous pair during meiosis

    // Per-gene mutation chance per unit of radiation during gamete formation.
    // Land radiation 0.2 gives 0.016 per gene: about 1 mutation per seed carrying ~60 gene copies.
    private const float MUTATION_RATE_PER_RADIATION = 0.08f;
    private const float DOMINANCE_FLIP_SHARE = 0.2f;              // Dominance flips at this share of the value mutation rate
    private const float MUTATION_STEP_PER_RADIATION = 5f;         // Typical allele change per unit of radiation (at least 1)
    private const float STRUCTURAL_CHANCE_PER_RADIATION = 0.05f;  // Chance per copied strand of gaining, losing, or swapping a locus

    // Cloning is the conservative strategy: it copies DNA with this share of the radiation a gamete experiences,
    // which means rarer changes and always the smallest step. Seeds are the source of larger variation.
    private const float CLONE_RADIATION_SHARE = 0.2f;
    private const float CLONE_STRUCTURAL_MIN_RADIATION = 0.4f;    // Clones can only gain/lose/swap loci above this tile radiation

    private TraitDefinition[] traitLookup; // indexed by runtime trait id
    // [OPTIMIZED] Cache flag to avoid repeated trait lookup rebuilds during runtime
    private bool traitLookupBuilt = false;

    [Header("References")]
    public TraitDatabase traitDatabase;

    void Start()
    {
        EnsureTraitLookup();
    }

    private void EnsureTraitLookup()
    {
        // [OPTIMIZED] Check cached flag instead of rebuilding each time
        if (!traitLookupBuilt || traitLookup == null || traitLookup.Length == 0)
        {
            BuildTraitLookup();
            traitLookupBuilt = true;
        }
    }

    internal PlantGenome GenerateNewGenome(EnvironmentData envData = null, bool adaptToEnvironment = false)
    {
        EnsureTraitLookup();

        PlantGenome newGenome = new PlantGenome();
        newGenome.generation = 0;
        newGenome.origin = "wild";
        newGenome.maternalDNA = new List<Allele>(64);
        newGenome.paternalDNA = new List<Allele>(64);

        // Ascending trait id keeps both strands sorted; both get the same loci with independent allele values
        for (int traitId = 0; traitId < traitLookup.Length; traitId++)
        {
            TraitDefinition trait = traitLookup[traitId];
            if (trait == null)
                continue;

            int alleleCount = Random.Range(trait.minLoci, trait.maxLoci + 1);
            for (int i = 0; i < alleleCount; i++)
            {
                newGenome.maternalDNA.Add(RandomAllele(traitId));
                newGenome.paternalDNA.Add(RandomAllele(traitId));
            }
        }

        // Environmental adaptation (bias allele values toward local conditions with slight variation)
        if (adaptToEnvironment)
        {
            ApplyEnvironmentalAdaptationToAlleles(newGenome, envData);
        }

        // DNA to Phenotype transformation
        newGenome.phenotypes = CalculatePhenotypes(newGenome);

        return newGenome;
    }

    /// <summary>
    /// Builds an offspring genome from two gametes (each a sorted strand, as produced by Meiosis).
    /// </summary>
    internal PlantGenome GenerateSeedGenome(List<Allele> maternalDNA, List<Allele> paternalDNA, int maternalGeneration = 0, int paternalGeneration = 0, string maternalOrigin = null, string paternalOrigin = null, string maternalSpeciesGuid = null, string paternalSpeciesGuid = null)
    {
        PlantGenome genome = new PlantGenome();
        genome.maternalDNA = new List<Allele>(maternalDNA);
        genome.paternalDNA = new List<Allele>(paternalDNA);
        genome.generation = NextGeneration(maternalGeneration, paternalGeneration);
        genome.origin = PlantGenome.CombineOriginTags(maternalOrigin, paternalOrigin);
        // Lineage identity survives only crosses within the same library species
        genome.speciesGuid = maternalSpeciesGuid == paternalSpeciesGuid ? maternalSpeciesGuid : null;
        genome.phenotypes = CalculatePhenotypes(genome);
        return genome;
    }

    /// <summary>
    /// Restores what isn't stored in save and species files: the strand order invariant and phenotypes.
    /// </summary>
    internal void RebuildLoadedGenomeDerivedData(PlantGenome genome)
    {
        if (genome == null)
            return;

        if (genome.maternalDNA == null)
        {
            genome.maternalDNA = new List<Allele>();
        }
        if (genome.paternalDNA == null)
        {
            genome.paternalDNA = new List<Allele>();
        }

        SortStrandByTrait(genome.maternalDNA);
        SortStrandByTrait(genome.paternalDNA);
        genome.phenotypes = CalculatePhenotypes(genome);
    }

    /// <summary>
    /// Stable insertion sort (copy order within a trait is preserved). Already-sorted strands cost one pass.
    /// </summary>
    private static void SortStrandByTrait(List<Allele> strand)
    {
        for (int i = 1; i < strand.Count; i++)
        {
            Allele allele = strand[i];
            int j = i - 1;
            while (j >= 0 && strand[j].traitId > allele.traitId)
            {
                strand[j + 1] = strand[j];
                j--;
            }
            strand[j + 1] = allele;
        }
    }

    /// <summary>
    /// Copying errors, applied in place to a strand being passed on: value changes, dominance flips, and
    /// occasionally a structural change. Radiation raises both how often mutations happen and how large value
    /// changes are. Clones pass a reduced radiation (see CloneGrass).
    /// </summary>
    private void ApplyMutations(List<Allele> strand, float radiation, bool allowStructuralChange)
    {
        float clampedRadiation = Mathf.Clamp01(radiation);
        float mutationChance = clampedRadiation * MUTATION_RATE_PER_RADIATION;
        float dominanceChance = mutationChance * DOMINANCE_FLIP_SHARE;

        for (int i = 0; i < strand.Count; i++)
        {
            Allele allele = strand[i];
            bool changed = false;

            if (Random.value < mutationChance)
            {
                int step = Mathf.Max(1, Mathf.RoundToInt(clampedRadiation * MUTATION_STEP_PER_RADIATION * Random.Range(0.5f, 1.5f)));
                allele.value = (byte)Mathf.Clamp(allele.value + step * RandomSign(), 0, MAX_ALLELE_VALUE);
                changed = true;
            }

            if (Random.value < dominanceChance)
            {
                allele.dominance = !allele.dominance;
                changed = true;
            }

            if (changed)
            {
                strand[i] = allele;
            }
        }

        if (allowStructuralChange && Random.value < clampedRadiation * STRUCTURAL_CHANCE_PER_RADIATION)
        {
            ApplyStructuralMutation(strand);
        }
    }

    /// <summary>
    /// Gains, loses, or swaps one locus while respecting each trait's min/max loci and the strand sort order.
    /// </summary>
    private void ApplyStructuralMutation(List<Allele> strand)
    {
        EnsureTraitLookup();

        // Track loci counts per trait to respect min/max loci constraints
        int[] lociCounts = new int[traitLookup.Length];
        for (int i = 0; i < strand.Count; i++)
        {
            int traitId = strand[i].traitId;
            if (traitId >= 0 && traitId < lociCounts.Length)
            {
                lociCounts[traitId]++;
            }
        }

        float roll = Random.value;
        bool applied = false;
        if (roll < 0.33f && strand.Count < MAX_DNA_LENGTH)
        {
            applied = TryAddLocus(strand, lociCounts);
        }
        else if (roll < 0.66f && strand.Count > 1)
        {
            applied = TryRemoveLocus(strand, lociCounts);
        }

        if (!applied && strand.Count > 0)
        {
            TryReplaceLocus(strand, lociCounts);
        }
    }

    private bool TryAddLocus(List<Allele> strand, int[] lociCounts)
    {
        // find traits below their maxLoci
        List<int> candidates = new List<int>();
        for (int t = 0; t < traitLookup.Length; t++)
        {
            if (traitLookup[t] != null && lociCounts[t] < traitLookup[t].maxLoci)
            {
                candidates.Add(t);
            }
        }
        if (candidates.Count == 0)
            return false;

        InsertLocus(strand, RandomAllele(candidates[Random.Range(0, candidates.Count)]));
        return true;
    }

    private bool TryRemoveLocus(List<Allele> strand, int[] lociCounts)
    {
        int removeIndex = Random.Range(0, strand.Count);
        if (!CanLoseLocus(strand[removeIndex].traitId, lociCounts))
            return false;

        strand.RemoveAt(removeIndex);
        return true;
    }

    private bool TryReplaceLocus(List<Allele> strand, int[] lociCounts)
    {
        int replaceIndex = Random.Range(0, strand.Count);
        Allele allele = strand[replaceIndex];
        int oldTrait = allele.traitId;
        if (!CanLoseLocus(oldTrait, lociCounts))
            return false;

        // gather eligible new traits
        List<int> candidates = new List<int>();
        for (int t = 0; t < traitLookup.Length; t++)
        {
            TraitDefinition def = traitLookup[t];
            if (def != null && t != oldTrait && lociCounts[t] < def.maxLoci)
            {
                candidates.Add(t);
            }
        }
        if (candidates.Count == 0)
            return false;

        // The locus moves to its new trait's group, with a fresh value
        strand.RemoveAt(replaceIndex);
        allele.traitId = candidates[Random.Range(0, candidates.Count)];
        allele.value = (byte)Random.Range(0, MAX_ALLELE_VALUE + 1);
        InsertLocus(strand, allele);
        return true;
    }

    private bool CanLoseLocus(int traitId, int[] lociCounts)
    {
        if (traitId < 0 || traitId >= traitLookup.Length || traitLookup[traitId] == null)
            return true; // A trait no longer in the database has no minimum
        return lociCounts[traitId] > traitLookup[traitId].minLoci;
    }

    /// <summary>
    /// Adds a locus as the last copy of its trait, keeping the strand sorted.
    /// </summary>
    private static void InsertLocus(List<Allele> strand, Allele allele)
    {
        int index = 0;
        while (index < strand.Count && strand[index].traitId <= allele.traitId)
        {
            index++;
        }
        strand.Insert(index, allele);
    }

    // [OPTIMIZED] Inline simple calculations when possible to reduce method call overhead
    internal PlantGenome CloneGrass(PlantGenome parent, float radiation)
    {
        PlantGenome clone = new PlantGenome();
        clone.generation = NextGeneration(parent.generation);
        clone.maternalDNA = new List<Allele>(parent.maternalDNA);
        clone.paternalDNA = new List<Allele>(parent.paternalDNA);

        float cloneRadiation = radiation * CLONE_RADIATION_SHARE;
        bool allowStructuralChange = radiation > CLONE_STRUCTURAL_MIN_RADIATION;
        ApplyMutations(clone.maternalDNA, cloneRadiation, allowStructuralChange);
        ApplyMutations(clone.paternalDNA, cloneRadiation, allowStructuralChange);

        clone.origin = parent.origin;
        clone.speciesGuid = parent.speciesGuid;
        clone.InheritGenus(parent);
        clone.phenotypes = CalculatePhenotypes(clone);
        return clone;
    }

    private static int NextGeneration(int parentA, int parentB = -1)
    {
        int parentGen = parentA;
        if (parentB >= 0 && parentB > parentGen)
        {
            parentGen = parentB;
        }
        if (parentGen >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return parentGen + 1;
    }

    /// <summary>
    /// Translates DNA to trait values and records how many loci the plant expresses (its upkeep basis).
    /// Each homologous pair contributes one value: the dominant allele if dominance differs, otherwise their average.
    /// A hemizygous copy contributes its own value. A trait is the mean of its loci mapped onto the trait's range.
    /// </summary>
    private float[] CalculatePhenotypes(PlantGenome genome)
    {
        EnsureTraitLookup();

        int traitCount = traitLookup.Length;
        float[] calcPhenotypes = new float[traitCount];

        // [OPTIMIZED] Single pass over loci, accumulating per trait. Replaces a Burst job whose
        // NativeArray allocation and scheduling cost far exceeded the work for ~23 traits.
        float[] totalAllele = new float[traitCount];
        int[] locusCount = new int[traitCount];
        List<Allele> maternal = genome.maternalDNA;
        List<Allele> paternal = genome.paternalDNA;

        int m = 0;
        int p = 0;
        int expressedLoci = 0;
        while (m < maternal.Count || p < paternal.Count)
        {
            int maternalTrait = m < maternal.Count ? maternal[m].traitId : int.MaxValue;
            int paternalTrait = p < paternal.Count ? paternal[p].traitId : int.MaxValue;
            int traitId = Mathf.Min(maternalTrait, paternalTrait);

            float value;
            if (maternalTrait == paternalTrait)
            {
                Allele mat = maternal[m++];
                Allele pat = paternal[p++];
                if (mat.dominance != pat.dominance)
                {
                    value = mat.dominance ? mat.value : pat.value;
                }
                else
                {
                    value = (mat.value + pat.value) * 0.5f;
                }
            }
            else if (maternalTrait < paternalTrait)
            {
                value = maternal[m++].value;
            }
            else
            {
                value = paternal[p++].value;
            }

            expressedLoci++;
            if (traitId >= 0 && traitId < traitCount)
            {
                totalAllele[traitId] += value;
                locusCount[traitId]++;
            }
        }
        genome.expressedLocusCount = expressedLoci;

        for (int traitId = 0; traitId < traitCount; traitId++)
        {
            TraitDefinition def = traitLookup[traitId];
            if (locusCount[traitId] == 0)
            {
                // No loci for this trait -> fallback to base value (or 0 if unset)
                calcPhenotypes[traitId] = def != null && def.baseValue > 0f ? def.baseValue : 0f;
                continue;
            }

            float minV = def != null ? def.minValue : 0f;
            float maxV = def != null ? def.maxValue : 0f;
            totalAllele[traitId] /= locusCount[traitId] * MAX_ALLELE_VALUE; // Now the trait's mean allele, 0..1
            calcPhenotypes[traitId] = Mathf.LerpUnclamped(minV, maxV, totalAllele[traitId]);
        }

        genome.lociPerTrait = locusCount;
        genome.normalizedTraitValues = totalAllele;
        return calcPhenotypes;
    }

    /// <summary>
    /// How genetically different two plants are: the share of their gene copies not matched by the other plant's
    /// gene counts, plus the mean allele difference over traits both carry (0..1 each, so 0..2 in total).
    /// Cheap (one pass over traits), so it can run once per plant per day.
    /// </summary>
    internal static float GeneticDistance(PlantGenome a, PlantGenome b)
    {
        int[] lociA = a.lociPerTrait;
        int[] lociB = b.lociPerTrait;
        if (lociA == null || lociB == null)
            return 0f;

        int unmatchedLoci = 0;
        int totalLoci = 0;
        int sharedTraits = 0;
        float valueDifference = 0f;
        int traitCount = Mathf.Min(lociA.Length, lociB.Length);
        for (int t = 0; t < traitCount; t++)
        {
            int countA = lociA[t];
            int countB = lociB[t];
            unmatchedLoci += countA > countB ? countA - countB : countB - countA;
            totalLoci += countA + countB;
            if (countA > 0 && countB > 0)
            {
                sharedTraits++;
                valueDifference += Mathf.Abs(a.normalizedTraitValues[t] - b.normalizedTraitValues[t]);
            }
        }

        float structural = totalLoci > 0 ? unmatchedLoci / (float)totalLoci : 0f;
        float values = sharedTraits > 0 ? valueDifference / sharedTraits : 0f;
        return structural + values;
    }

    private void BuildTraitLookup()
    {
        TraitDefinition[] traits = traitDatabase.traits;
        int maxTraitId = -1;
        for (int i = 0; i < traits.Length; i++)
        {
            int traitId = GetTraitId(traits[i], i);
            if (traitId > maxTraitId)
            {
                maxTraitId = traitId;
            }
        }

        traitLookup = new TraitDefinition[maxTraitId + 1];
        for (int i = 0; i < traits.Length; i++)
        {
            int traitId = GetTraitId(traits[i], i);
            traitLookup[traitId] = traits[i];
        }
    }

    // [OPTIMIZED] Inline method for performance
    private int GetTraitId(TraitDefinition trait, int fallbackIndex)
    {
        return trait.id >= 0 ? trait.id : fallbackIndex;
    }

    /// <summary>
    /// Simulates meiosis for a parent genome: walks the two strands pair by pair with crossover, then applies
    /// copying errors to the resulting gamete. A hemizygous copy is inherited only if the gamete is reading its
    /// strand there, so a gamete's copy count for a trait stays between the two strands' counts. Since both
    /// strands respect a trait's min/max loci, so does every gamete.
    /// </summary>
    // [OPTIMIZED] Pre-allocates gamete list to exact loci count to minimize reallocation
    internal List<Allele> Meiosis(PlantGenome parent, float radiation)
    {
        if (parent == null || parent.maternalDNA == null || parent.paternalDNA == null)
        {
            return new List<Allele>();
        }

        List<Allele> maternal = parent.maternalDNA;
        List<Allele> paternal = parent.paternalDNA;
        // [OPTIMIZED] Pre-allocate with exact capacity to avoid list growth during meiosis
        List<Allele> gamete = new List<Allele>(Mathf.Max(maternal.Count, paternal.Count));

        bool useMaternal = Random.value < 0.5f; // start with a random strand

        int m = 0;
        int p = 0;
        while (m < maternal.Count || p < paternal.Count)
        {
            int maternalTrait = m < maternal.Count ? maternal[m].traitId : int.MaxValue;
            int paternalTrait = p < paternal.Count ? paternal[p].traitId : int.MaxValue;

            if (maternalTrait == paternalTrait)
            {
                // Random crossover only matters when both strands exist at this locus
                if (Random.value < CROSSOVER_RATE)
                {
                    useMaternal = !useMaternal;
                }
                gamete.Add(useMaternal ? maternal[m] : paternal[p]);
                m++;
                p++;
            }
            else if (maternalTrait < paternalTrait)
            {
                if (useMaternal)
                {
                    gamete.Add(maternal[m]);
                }
                m++;
            }
            else
            {
                if (!useMaternal)
                {
                    gamete.Add(paternal[p]);
                }
                p++;
            }
        }

        // Final pass: copying errors, including structural mutation so gametes can gain/lose loci
        ApplyMutations(gamete, radiation, true);
        return gamete;
    }

    private void ApplyEnvironmentalAdaptationToAlleles(PlantGenome genome, EnvironmentData envData)
    {
        if (traitLookup == null || traitLookup.Length == 0)
            return;
        if (envData == null)
            return;
        if (genome.maternalDNA == null || genome.paternalDNA == null)
            return;

        int loci = Mathf.Min(genome.maternalDNA.Count, genome.paternalDNA.Count);
        for (int i = 0; i < loci; i++)
        {
            int traitId = genome.maternalDNA[i].traitId;
            int targetAllele = -1;

            switch (traitId)
            {
                case TraitId.TemperatureTolerance: // temperature tolerance
                    targetAllele = ComputeTargetAlleleValue(envData.temperature, traitId, 5f);
                    break;
                case TraitId.HydrationRequirement: // hydration requirement
                    targetAllele = ComputeTargetAlleleValue(envData.hydration, traitId, 10f);
                    break;
                default:
                    continue;
            }

            if (targetAllele < 0)
                continue;

            // Apply to both maternal and paternal DNA
            Allele mat = genome.maternalDNA[i];
            Allele pat = genome.paternalDNA[i];
            mat.value = (byte)targetAllele;
            pat.value = (byte)targetAllele;
            genome.maternalDNA[i] = mat;
            genome.paternalDNA[i] = pat;
        }
    }

    private int ComputeTargetAlleleValue(float envValue, int traitId, float jitter)
    {
        if (traitId < 0 || traitLookup == null || traitId >= traitLookup.Length)
            return -1;

        TraitDefinition def = traitLookup[traitId];
        if (def == null)
            return -1;

        float span = def.maxValue - def.minValue;
        if (Mathf.Abs(span) < 0.0001f)
            return -1;

        float perturbed = envValue + Random.Range(-jitter, jitter);
        float normalized = Mathf.InverseLerp(def.minValue, def.maxValue, perturbed);
        float clampedNorm = Mathf.Clamp01(normalized);
        int targetAllele = Mathf.RoundToInt(clampedNorm * MAX_ALLELE_VALUE);
        targetAllele = Mathf.Clamp(targetAllele, 0, MAX_ALLELE_VALUE);
        return targetAllele;
    }

    private Allele RandomAllele(int traitId)
    {
        return new Allele((byte)Random.Range(0, MAX_ALLELE_VALUE + 1), randomBool(), traitId);
    }

    bool randomBool()
    {
        if (Random.value < 0.5f)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    int RandomSign()
    {
        return Random.value < 0.5f ? -1 : 1;
    }
}
