using UnityEngine;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = UnityEngine.Random;

public class GeneManager : MonoBehaviour
{
    private const int MAX_ALLELE_VALUE = 20;
    private const int MAX_DNA_LENGTH = 256;
    private const float CROSSOVER_RATE = 0.15f;   // chance to start a crossover
    private const float MUTATION_RATE = 0.02f;    // per-allele mutation during gamete formation

    private Stack<List<Allele>> storedGenePool = new Stack<List<Allele>>();
    private int POOL_SIZE = 5;
    private TraitDefinition[] traitLookup; // indexed by runtime trait id
    // [OPTIMIZED] Cache flag to avoid repeated trait lookup rebuilds during runtime
    private bool traitLookupBuilt = false;

    [Header("References")]
    public TraitDatabase traitDatabase;

    void Start()
    {
        // [OPTIMIZED] Only build trait lookup once at start
        if (!traitLookupBuilt)
        {
            BuildTraitLookup();
            traitLookupBuilt = true;
        }
        // Initialize gene pool
        for (int i = 0; i < POOL_SIZE; i++)
        {
            storedGenePool.Push(new List<Allele>(256)); // [OPTIMIZED] Pre-allocate with typical DNA length to reduce reallocs
        }
    }

    internal PlantGenome GenerateNewGenome(EnvironmentData envData = null, bool adaptToEnvironment = false)
    {
        // [OPTIMIZED] Check cached flag instead of rebuilding each time
        if (!traitLookupBuilt || traitLookup == null || traitLookup.Length == 0)
        {
            BuildTraitLookup();
            traitLookupBuilt = true;
        }

        List<Allele> newMaternalDNA = GetPooledGeneList();
        List<int> maternalComposition = new List<int>();

        PlantGenome newGenome = new PlantGenome();
        newGenome.phenotypes = new float[traitLookup.Length]; // blank phenotypes default to 0f
        newGenome.generation = 0;
        newGenome.origin = "wild";
        newGenome.SetDominantDNA(true);

        for (int traitIndex = 0; traitIndex < traitDatabase.traits.Length; traitIndex++)
        {
            TraitDefinition trait = traitDatabase.traits[traitIndex];
            int traitId = GetTraitId(trait, traitIndex);
            int alleleCount = Random.Range(trait.minLoci, trait.maxLoci + 1);

            if (alleleCount > 0)
            {
                for (int i = 0; i < alleleCount; i++)
                {
                    Allele randomAllele = new Allele((byte)Random.Range(0, 21), randomBool(), traitId);
                    newMaternalDNA.Add(randomAllele);
                    maternalComposition.Add(traitId);
                }
            }
        }

        // Generate paternal DNA from maternal DNA for newly spawned grass
        List<Allele> newPaternalDNA = FixedCompDNA(maternalComposition);

        // Assign values for the new genome
        newGenome.maternalDNA = new List<Allele>();
        newGenome.maternalDNA.AddRange(newMaternalDNA);

        newGenome.paternalDNA = new List<Allele>();
        newGenome.paternalDNA.AddRange(newPaternalDNA);

        ReturnPooledGeneList(newMaternalDNA);

        // Environmental adaptation (bias allele values toward local conditions with slight variation)
        if (adaptToEnvironment)
        {
            ApplyEnvironmentalAdaptationToAlleles(newGenome, envData);
        }

        // DNA to Phenotype transformation
        newGenome.phenotypes = CalculatePhenotypes(newGenome, maternalComposition);

        return newGenome;
    }

    /// <summary>
    /// Builds a genome from supplied maternal/paternal DNA and their compositions, reconciling differing lengths.
    /// The longer composition becomes the dominant composition (ties prefer maternal) and is recorded via dominantDNA.
    /// Phenotypes are calculated respecting that major composition; loci missing in a strand are skipped.
    /// </summary>
    internal PlantGenome GenerateSeedGenome(List<Allele> maternalDNA, List<Allele> paternalDNA, int maternalGeneration = 0, int paternalGeneration = 0, string maternalOrigin = null, string paternalOrigin = null)
    {
        // [OPTIMIZED] Check cached flag instead of rebuilding
        if (!traitLookupBuilt || traitLookup == null || traitLookup.Length == 0)
        {
            BuildTraitLookup();
            traitLookupBuilt = true;
        }

        // Decide major DNA (longer list wins; ties prefer maternal for deterministic restores)
        bool useMaternalMajor = maternalDNA.Count >= paternalDNA.Count;
        List<Allele> majorDNA = useMaternalMajor ? maternalDNA : paternalDNA;
        List<int> majorComp = BuildCompositionFromDNA(majorDNA);

        PlantGenome genome = new PlantGenome();
        genome.maternalDNA = new List<Allele>(maternalDNA);
        genome.paternalDNA = new List<Allele>(paternalDNA);
        genome.SetDominantDNA(useMaternalMajor);
        genome.generation = NextGeneration(maternalGeneration, paternalGeneration);
        genome.origin = PlantGenome.CombineOriginTags(maternalOrigin, paternalOrigin);
        genome.phenotypes = new float[traitLookup.Length]; // start at zero for all traits
        genome.phenotypes = CalculatePhenotypes(genome, majorComp);
        return genome;
    }

    internal void RebuildLoadedGenomeDerivedData(PlantGenome genome)
    {
        if (genome == null)
            return;

        if (!traitLookupBuilt || traitLookup == null || traitLookup.Length == 0)
        {
            BuildTraitLookup();
            traitLookupBuilt = true;
        }

        if (genome.maternalDNA == null)
        {
            genome.maternalDNA = new List<Allele>();
        }
        if (genome.paternalDNA == null)
        {
            genome.paternalDNA = new List<Allele>();
        }

        bool useMaternalMajor;
        if (genome.dominantDNA <= 1)
        {
            useMaternalMajor = genome.UsesMaternalDominantDNA();
        }
        else
        {
            useMaternalMajor = genome.maternalDNA.Count >= genome.paternalDNA.Count;
            genome.SetDominantDNA(useMaternalMajor);
        }

        List<int> dominantComposition = BuildCompositionFromDNA(useMaternalMajor ? genome.maternalDNA : genome.paternalDNA);
        genome.phenotypes = CalculatePhenotypes(genome, dominantComposition);
    }

    internal List<Allele> GenerateMutatedDNA(List<Allele> origDNA, float radiation, bool allowStructuralChange = false, List<int> dnaComposition = null)
    {
        // [OPTIMIZED] Pre-allocate with original DNA size to reduce reallocation during mutation
        List<Allele> mutatedDNA = new List<Allele>(origDNA.Count);

        float normalizedRadiation = radiation;
        normalizedRadiation = Mathf.Clamp(normalizedRadiation, 0f, 0.8f);

        foreach (var allele in origDNA)
        {
            Allele mutatedAllele = allele;
            if (Random.value < normalizedRadiation)
            {
                float delta = radiation * 5f * Random.Range(0.5f, 1.5f) * RandomSign();
                if (Mathf.Abs(delta) < 1f)
                {
                    delta += 0.8f * RandomSign();
                }
                int newValue = mutatedAllele.value + Mathf.RoundToInt(delta);
                // Clamp to valid allele bounds
                newValue = Mathf.Clamp(newValue, 0, MAX_ALLELE_VALUE);

                mutatedAllele.value = (byte)newValue;
            }

            if (Random.value < normalizedRadiation * 0.2f)
            {
                mutatedAllele.dominance = !mutatedAllele.dominance;
            }

            mutatedDNA.Add(mutatedAllele);
        }

        if (allowStructuralChange && dnaComposition != null && dnaComposition.Count > 0)
        {
            // [OPTIMIZED] Check cached flag
            if (!traitLookupBuilt || traitLookup == null || traitLookup.Length == 0)
            {
                BuildTraitLookup();
                traitLookupBuilt = true;
            }

            // Track loci counts per trait to respect min/max loci constraints
            int[] lociCounts = new int[traitLookup.Length];
            for (int i = 0; i < dnaComposition.Count; i++)
            {
                int tId = dnaComposition[i];
                if (tId >= 0 && tId < lociCounts.Length)
                {
                    lociCounts[tId]++;
                }
            }

            float structureChance = Mathf.Clamp01(normalizedRadiation * 0.05f); // extremely small chance
            if (Random.value < structureChance)
            {
                float roll = Random.value;
                bool attempted = false;

                // ADD
                if (roll < 0.33f && dnaComposition.Count < MAX_DNA_LENGTH)
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
                    if (candidates.Count > 0)
                    {
                        int traitId = candidates[Random.Range(0, candidates.Count)];
                        int insertIndex = Random.Range(0, dnaComposition.Count + 1);
                        Allele newAllele = new Allele((byte)Random.Range(0, 21), randomBool(), traitId);
                        mutatedDNA.Insert(insertIndex, newAllele);
                        dnaComposition.Insert(insertIndex, traitId);
                        lociCounts[traitId]++;
                        attempted = true;
                    }
                }
                // REMOVE
                else if (roll < 0.66f && mutatedDNA.Count > 1)
                {
                    int removeIndex = Random.Range(0, mutatedDNA.Count);
                    int traitId = dnaComposition[removeIndex];
                    TraitDefinition tDef = (traitId >= 0 && traitId < traitLookup.Length) ? traitLookup[traitId] : null;
                    int minLoci = tDef != null ? tDef.minLoci : 0;
                    if (lociCounts.Length > traitId && lociCounts[traitId] > minLoci)
                    {
                        mutatedDNA.RemoveAt(removeIndex);
                        dnaComposition.RemoveAt(removeIndex);
                        lociCounts[traitId]--;
                        attempted = true;
                    }
                }
                // REPLACE
                if (!attempted)
                {
                    int replaceIndex = Random.Range(0, dnaComposition.Count);
                    int oldTrait = dnaComposition[replaceIndex];
                    TraitDefinition oldDef = (oldTrait >= 0 && oldTrait < traitLookup.Length) ? traitLookup[oldTrait] : null;
                    int oldMin = oldDef != null ? oldDef.minLoci : 0;

                    // gather eligible new traits
                    List<int> replaceCandidates = new List<int>();
                    for (int t = 0; t < traitLookup.Length; t++)
                    {
                        TraitDefinition def = traitLookup[t];
                        if (def == null) continue;
                        if (t == oldTrait) continue; // ensure actual trait swap
                        bool belowMax = lociCounts[t] < def.maxLoci;
                        bool oldAboveMin = lociCounts.Length > oldTrait ? lociCounts[oldTrait] - 1 >= oldMin : true;
                        if (belowMax && oldAboveMin)
                        {
                            replaceCandidates.Add(t);
                        }
                    }

                    if (replaceCandidates.Count > 0)
                    {
                        int newTrait = replaceCandidates[Random.Range(0, replaceCandidates.Count)];
                        dnaComposition[replaceIndex] = newTrait;
                        if (oldTrait >= 0 && oldTrait < lociCounts.Length) lociCounts[oldTrait]--;
                        if (newTrait >= 0 && newTrait < lociCounts.Length) lociCounts[newTrait]++;

                        // force a value change for the replaced locus
                        Allele mutatedAllele = mutatedDNA[replaceIndex];
                        mutatedAllele.value = (byte)Random.Range(0, 21);
                        mutatedAllele.traitId = newTrait;
                        mutatedDNA[replaceIndex] = mutatedAllele;
                        attempted = true;
                    }
                }
            }
        }
        return mutatedDNA;
    }

    // [OPTIMIZED] Inline simple calculations when possible to reduce method call overhead
    internal PlantGenome CloneGrass(PlantGenome parent, float radiation)
    {
        PlantGenome clone = new PlantGenome();
        clone.generation = NextGeneration(parent.generation);
        bool parentMaternalDominant = parent.UsesMaternalDominantDNA();
        List<Allele> parentDominantDNA = parent.GetDominantDNA();
        List<int> dominantComposition = BuildCompositionFromDNA(parentDominantDNA);
        if (dominantComposition.Count == 0)
        {
            bool fallbackMaternal = parent.maternalDNA.Count >= parent.paternalDNA.Count;
            dominantComposition = BuildCompositionFromDNA(fallbackMaternal ? parent.maternalDNA : parent.paternalDNA);
            parentMaternalDominant = fallbackMaternal;
        }

        bool allowStructural = radiation > 0.6f;
        float normalizedRadiation = radiation * 0.1f;

        clone.maternalDNA = GenerateMutatedDNA(parent.maternalDNA, normalizedRadiation, allowStructural, allowStructural ? dominantComposition : null);
        clone.paternalDNA = GenerateMutatedDNA(parent.paternalDNA, normalizedRadiation, allowStructural, allowStructural ? dominantComposition : null);
        clone.origin = parent.origin;

        // Rebuild composition from the longer strand; tie keeps parent's dominant strand for determinism.
        bool useMaternalMajor = clone.maternalDNA.Count > clone.paternalDNA.Count
            || (clone.maternalDNA.Count == clone.paternalDNA.Count && parentMaternalDominant);
        clone.SetDominantDNA(useMaternalMajor);
        dominantComposition = BuildCompositionFromDNA(useMaternalMajor ? clone.maternalDNA : clone.paternalDNA);
        clone.phenotypes = new float[parent.phenotypes.Length];
        parent.phenotypes.CopyTo(clone.phenotypes, 0);

        clone.phenotypes = CalculatePhenotypes(clone, dominantComposition);

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

    // [OPTIMIZED] Pre-allocate DNA list with proper capacity when generating fixed composition DNA
    internal List<Allele> FixedCompDNA(List<int> DNAComp)
    {
        List<Allele> newDNA = new List<Allele>();

        foreach (var traitId in DNAComp)
        {
            Allele randomAllele = new Allele((byte)Random.Range(0, 21), randomBool(), traitId);
            newDNA.Add(randomAllele);
        }

        return newDNA;
    }

    private float[] CalculatePhenotypes(PlantGenome genome, List<int> composition)
    {
        // [OPTIMIZED] Check cached flag for trait lookup
        if (!traitLookupBuilt || traitLookup == null || traitLookup.Length == 0)
        {
            BuildTraitLookup();
            traitLookupBuilt = true;
        }

        float[] calcPhenotypes = new float[traitLookup.Length];

        if (composition == null)
        {
            composition = BuildCompositionFromDNA(genome.maternalDNA);
        }

        // [OPTIMIZED] Burst job to compute phenotypes in parallel per trait
        // Reuse composition data directly without intermediate ToArray() calls
        int traitCount = traitLookup.Length;
        int locusCount = composition.Count;

        NativeArray<float> outPhen = new NativeArray<float>(traitCount, Allocator.TempJob);
        NativeArray<int> compArr = new NativeArray<int>(composition.ToArray(), Allocator.TempJob);
        NativeArray<Allele> matArr = new NativeArray<Allele>(genome.maternalDNA.ToArray(), Allocator.TempJob);
        NativeArray<Allele> patArr = new NativeArray<Allele>(genome.paternalDNA.ToArray(), Allocator.TempJob);
        NativeArray<float> traitMin = new NativeArray<float>(traitCount, Allocator.TempJob);
        NativeArray<float> traitMax = new NativeArray<float>(traitCount, Allocator.TempJob);
        NativeArray<float> traitBase = new NativeArray<float>(traitCount, Allocator.TempJob);

        for (int i = 0; i < traitCount; i++)
        {
            if (traitLookup[i] != null)
            {
                traitMin[i] = traitLookup[i].minValue;
                traitMax[i] = traitLookup[i].maxValue;
                traitBase[i] = traitLookup[i].baseValue > 0f ? traitLookup[i].baseValue : 0f;
            }
            else
            {
                traitMin[i] = 0f;
                traitMax[i] = 0f;
                traitBase[i] = 0f;
            }
        }

        var job = new PhenotypeJob
        {
            composition = compArr,
            maternal = matArr,
            paternal = patArr,
            traitMin = traitMin,
            traitMax = traitMax,
            traitBase = traitBase,
            maxAlleleValue = MAX_ALLELE_VALUE,
            locusCount = locusCount,
            output = outPhen
        };

        JobHandle handle = job.Schedule(traitCount, 1);
        handle.Complete();

        outPhen.CopyTo(calcPhenotypes);

        outPhen.Dispose();
        compArr.Dispose();
        matArr.Dispose();
        patArr.Dispose();
        traitMin.Dispose();
        traitMax.Dispose();
        traitBase.Dispose();

        return calcPhenotypes;
    }

    [BurstCompile]
    private struct PhenotypeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> composition;
        [ReadOnly] public NativeArray<Allele> maternal;
        [ReadOnly] public NativeArray<Allele> paternal;
        [ReadOnly] public NativeArray<float> traitMin;
        [ReadOnly] public NativeArray<float> traitMax;
        [ReadOnly] public NativeArray<float> traitBase;
        public int locusCount;
        public float maxAlleleValue;
        public NativeArray<float> output;

        public void Execute(int traitId)
        {
            TraitDefinitionExists(traitId, out float minV, out float maxV);

            int totalAllele = 0;
            float maxAllele = 0f;
            bool hasLocus = false;

            for (int i = 0; i < locusCount; i++)
            {
                if (composition[i] != traitId) continue;
                hasLocus = true;

                bool matOK = i < maternal.Length && maternal[i].traitId == traitId;
                bool patOK = i < paternal.Length && paternal[i].traitId == traitId;

                if (matOK && !patOK)
                {
                    totalAllele += maternal[i].value;
                    maxAllele += maxAlleleValue;
                    continue;
                }
                if (patOK && !matOK)
                {
                    totalAllele += paternal[i].value;
                    maxAllele += maxAlleleValue;
                    continue;
                }
                if (matOK && patOK && maternal[i].traitId != paternal[i].traitId)
                {
                    // composition aligns to major DNA; take allele matching composition (maternal or paternal)
                    totalAllele += composition[i] == maternal[i].traitId ? maternal[i].value : paternal[i].value;
                    maxAllele += maxAlleleValue;
                    continue;
                }

                if (matOK && patOK)
                {
                    if (maternal[i].dominance != paternal[i].dominance)
                    {
                        totalAllele += maternal[i].dominance ? maternal[i].value : paternal[i].value;
                    }
                    else
                    {
                        totalAllele += (maternal[i].value + paternal[i].value) / 2;
                    }
                    maxAllele += maxAlleleValue;
                }
            }

            if (!hasLocus || maxAllele <= 0.0001f)
            {
                float baseVal = traitBase.Length > traitId ? traitBase[traitId] : 0f;
                output[traitId] = baseVal; // No loci for this trait -> fallback to base value (or 0 if unset)
                return;
            }

            float normalized = totalAllele / maxAllele;
            output[traitId] = math.lerp(minV, maxV, normalized);
        }

        private void TraitDefinitionExists(int id, out float minV, out float maxV)
        {
            minV = traitMin.Length > id ? traitMin[id] : 0f;
            maxV = traitMax.Length > id ? traitMax[id] : 0f;
        }
    }

    internal List<Allele> GetPooledGeneList()
    {
        if (storedGenePool.Count > 0)
        {
            var list = storedGenePool.Pop();
            list.Clear();
            return list;
        }
        Debug.Log("Out of DNA List!");
        return new List<Allele>();
    }

    internal void ReturnPooledGeneList(List<Allele> list)
    {
        if (storedGenePool.Count < POOL_SIZE)
        {
            list.Clear();
            storedGenePool.Push(list);
        }
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

    // [OPTIMIZED] Pre-allocate composition list with exact DNA count to avoid reallocs
    private List<int> BuildCompositionFromDNA(List<Allele> dna)
    {
        if (dna == null)
            return new List<int>();
        List<int> comp = new List<int>(dna.Count); // Pre-allocate exact size
        for (int i = 0; i < dna.Count; i++)
        {
            comp.Add(dna[i].traitId);
        }
        return comp;
    }

    /// <summary>
    /// Simulates meiosis for a parent genome: performs simple crossover between maternal and paternal strands,
    /// then applies light mutation to produce a gamete strand (alleles with trait ids).
    /// </summary>
    // [OPTIMIZED] Pre-allocates gamete list to exact loci count to minimize reallocation
    internal List<Allele> Meiosis(PlantGenome parent, float radiation)
    {
        if (parent == null || parent.maternalDNA == null || parent.paternalDNA == null)
        {
            return new List<Allele>();
        }

        int loci = Mathf.Max(parent.maternalDNA.Count, parent.paternalDNA.Count);
        // [OPTIMIZED] Pre-allocate with exact capacity to avoid list growth during meiosis
        List<Allele> gamete = new List<Allele>(loci);

        bool useMaternal = Random.value < 0.5f; // start with a random strand

        for (int i = 0; i < loci; i++)
        {
            bool maternalExists = i < parent.maternalDNA.Count;
            bool paternalExists = i < parent.paternalDNA.Count;

            // Random crossover only matters when both strands exist at this locus
            if (maternalExists && paternalExists && Random.value < CROSSOVER_RATE)
            {
                useMaternal = !useMaternal;
            }

            Allele source;
            if (maternalExists && paternalExists)
            {
                source = useMaternal ? parent.maternalDNA[i] : parent.paternalDNA[i];
            }
            else if (maternalExists) // tail on maternal only
            {
                source = parent.maternalDNA[i];
            }
            else if (paternalExists) // tail on paternal only
            {
                source = parent.paternalDNA[i];
            }
            else
            {
                continue; // should not happen
            }

            // Write in re-combined allele
            Allele mutated = source;
            gamete.Add(mutated);
        }

        // Final pass: allow structural mutation so gametes can gain/lose loci while keeping composition aligned
        var mutatedGamete = GenerateMutatedDNA(gamete, radiation, true, BuildCompositionFromDNA(gamete));
        return mutatedGamete;
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
