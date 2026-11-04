using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A named species within a genus. Wild genera have one for now; see GenusTracker.AddNextSpecies.
/// </summary>
internal class SpeciesRecord
{
    public string name;
    public string speciesGuid;      // Library species identity; null for generated species
}

/// <summary>
/// One reproductive group of plants. Its colour is its founder's and never changes.
/// </summary>
internal class GenusRecord
{
    public int id;
    public string name;
    public bool isLibrary;          // Named after a saved species; kept apart from generated genera
    public int parentGenusId;       // Genus it split from; PlantGenome.NO_GENUS for original lineages
    public long foundedDay;
    public Color founderColor;
    public List<SpeciesRecord> species = new List<SpeciesRecord>();
    public int nextSpeciesIndex = 1;
    public PlantGenome[] representatives = new PlantGenome[GenusTracker.REPRESENTATIVES]; // Recent members; slots may be null
    public int plants;              // Recounted daily and at each grouping
    public int seeds;               // Kept current as seeds are made and die (see Seed.genusId)

    // Working state during GroupPlants: reservoir sample for the next representatives
    public PlantGenome[] nextRepresentatives = new PlantGenome[GenusTracker.REPRESENTATIVES];
    public int candidatesSeen;

    public SpeciesRecord FindSpecies(string speciesName)
    {
        for (int i = 0; i < species.Count; i++)
        {
            if (string.Equals(species[i].name, speciesName, System.StringComparison.OrdinalIgnoreCase))
                return species[i];
        }
        return null;
    }
}

/// <summary>
/// Generated taxon names. Index 1 is the first name: a, b ... z, aa, ab ... zz, aaa.
/// Genera capitalize the first letter (A ... Z, Aa, Ab), species stay lower case.
/// </summary>
internal static class BinomialNames
{
    internal static string Sequence(int index)
    {
        string name = "";
        while (index > 0)
        {
            index--; // Bijective base 26: there is no zero digit, so "z" is followed by "aa"
            name = (char)('a' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    internal static string GenusName(int index)
    {
        string name = Sequence(index);
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }
}

/// <summary>
/// Groups plants into genera: populations that can no longer breed with each other.
/// Plants inherit their mother's genus at birth. Every few real minutes (GroupPlants) each living plant is measured
/// against a few representatives of its genus and stays if it could breed with any of them, so the routine cost is a
/// few GeneticDistance calls per plant however many genera exist. Only plants that fit none are compared with other
/// genera, and those that fit nowhere found a new genus if enough of them form a group (lone oddballs stay put).
/// Representatives are random current members, re-picked at each grouping, so a genus can drift over time without being
/// split, while a subgroup that has become unable to breed with the rest splits off.
/// Seeds are checked only twice: once when they are dispersed (ClassifySeed) and once when they sprout (PlaceSprout).
/// Each seed records the genus it was counted in, so seed counts stay exact between groupings and a genus lives on
/// while any of its seeds do. Plant counts are recounted cheaply once a day (RecountPlants) for the research log.
/// Genera never merge, and plants in different genera cannot breed (see GrassManager.SpreadPollen).
/// Library genera carry a saved name and stay apart: nothing joins them or leaves them for a generated genus.
/// Speciation within a genus is not implemented yet; a future pass would call AddNextSpecies and assign the new
/// name to the diverging group's genomes.
/// </summary>
internal class GenusTracker
{
    internal const int REPRESENTATIVES = 3;
    private const int MIN_SPLIT_GROUP = 5; // Fewer outliers than this stay in their genus rather than founding a new one

    private readonly Dictionary<int, GenusRecord> genera = new Dictionary<int, GenusRecord>();
    private readonly List<PlantGenome> outliers = new List<PlantGenome>();
    private readonly List<PlantGenome> unplaced = new List<PlantGenome>();
    private readonly List<PlantGenome> group = new List<PlantGenome>();
    private readonly List<int> extinctIds = new List<int>();
    private int nextGenusId = 1;
    private int nextGenusNameIndex = 1;

    internal IEnumerable<GenusRecord> Genera => genera.Values;
    internal int Count => genera.Count;
    internal int NextGenusId => nextGenusId;
    internal int NextGenusNameIndex => nextGenusNameIndex;

    internal bool TryGetGenus(int genusId, out GenusRecord genus)
    {
        return genera.TryGetValue(genusId, out genus);
    }

    /// <summary>
    /// Places a new wild genome into the closest generated genus it can breed with, or founds a new one.
    /// Costs a few GeneticDistance calls per genus.
    /// </summary>
    internal void Assign(PlantGenome genome, float maxBreedingDistance, long day)
    {
        GenusRecord genus = FindClosestGenus(genome, maxBreedingDistance, false);
        if (genus == null)
        {
            genus = Found(genome, PlantGenome.NO_GENUS, day);
        }
        Join(genome, genus, null);
    }

    /// <summary>
    /// Places a saved species' genome under its saved names, without comparing it to any generated genus.
    /// Saved species sharing a genus name share one genus.
    /// </summary>
    internal void AssignLibrary(PlantGenome genome, string genusName, string speciesName, string speciesGuid, long day)
    {
        GenusRecord genus = FindLibraryGenus(genusName);
        if (genus == null)
        {
            genus = Found(genome, PlantGenome.NO_GENUS, day, genusName);
        }

        SpeciesRecord species = genus.FindSpecies(speciesName);
        if (species == null)
        {
            species = new SpeciesRecord { name = speciesName, speciesGuid = speciesGuid };
            genus.species.Add(species);
        }
        Join(genome, genus, species);
    }

    /// <summary>
    /// Adds the next generated species to a genus (a, b, c ...). Used for a generated genus's first species,
    /// and the entry point for speciation within a genus.
    /// </summary>
    internal SpeciesRecord AddNextSpecies(GenusRecord genus)
    {
        string name;
        do
        {
            name = BinomialNames.Sequence(genus.nextSpeciesIndex++);
        } while (genus.FindSpecies(name) != null);

        SpeciesRecord species = new SpeciesRecord { name = name };
        genus.species.Add(species);
        return species;
    }

    /// <summary>
    /// Applies a library rename to the genus records. Returns the genus the species now belongs to, or null if no
    /// living population carries it. oldGenusId tells the caller which genomes to update.
    /// </summary>
    internal GenusRecord RenameLibrarySpecies(string speciesGuid, string newGenusName, string newSpeciesName, long day, out int oldGenusId)
    {
        oldGenusId = PlantGenome.NO_GENUS;
        GenusRecord current = null;
        SpeciesRecord species = null;
        foreach (GenusRecord genus in genera.Values)
        {
            if (!genus.isLibrary)
                continue;
            for (int i = 0; i < genus.species.Count && species == null; i++)
            {
                if (genus.species[i].speciesGuid == speciesGuid)
                {
                    species = genus.species[i];
                    current = genus;
                }
            }
        }
        if (current == null)
            return null;

        oldGenusId = current.id;
        species.name = newSpeciesName;

        GenusRecord target = FindLibraryGenus(newGenusName);
        if (target == current || target == null && current.species.Count == 1)
        {
            current.name = newGenusName; // Includes case-only changes; a sole species takes its genus with it
            return current;
        }

        current.species.Remove(species);
        if (target == null)
        {
            target = new GenusRecord
            {
                id = nextGenusId++,
                name = newGenusName,
                isLibrary = true,
                parentGenusId = PlantGenome.NO_GENUS,
                foundedDay = day,
                founderColor = current.founderColor
            };
            System.Array.Copy(current.representatives, target.representatives, REPRESENTATIVES);
            genera[target.id] = target;
            ResearchCounters.Today.generaFounded++;
        }
        target.species.Add(species);
        return target;
    }

    /// <summary>
    /// The periodic pass over all living plants: recounts them, re-picks representatives, moves plants that no longer
    /// fit their genus, and removes genera with no plants or seeds left (unless removeExtinct is false).
    /// A genus with only seeds keeps its previous representatives.
    /// </summary>
    internal void GroupPlants(List<PlantGenome> plantGenomes, float maxBreedingDistance, long day, bool removeExtinct = true)
    {
        foreach (GenusRecord genus in genera.Values)
        {
            genus.plants = 0;
            System.Array.Clear(genus.nextRepresentatives, 0, REPRESENTATIVES);
            genus.candidatesSeen = 0;
        }

        outliers.Clear();
        for (int i = 0; i < plantGenomes.Count; i++)
        {
            PlantGenome genome = plantGenomes[i];
            if (!FitsOwnGenus(genome, maxBreedingDistance, out GenusRecord genus))
            {
                outliers.Add(genome);
                continue;
            }
            genus.plants++;
            ConsiderAsRepresentative(genus, genome);
        }

        PlaceOutliers(maxBreedingDistance, day);

        foreach (GenusRecord genus in genera.Values)
        {
            if (genus.nextRepresentatives[0] != null)
            {
                System.Array.Copy(genus.nextRepresentatives, genus.representatives, REPRESENTATIVES);
            }
        }
        if (removeExtinct)
        {
            RemoveExtinct();
        }
    }

    /// <summary>
    /// Cheap daily recount of plants by the genus they carry (no genetic comparisons), then removes genera with no
    /// plants or seeds left. Keeps the research log's per-genus counts current between groupings.
    /// </summary>
    internal void RecountPlants(List<PlantGenome> plantGenomes)
    {
        foreach (GenusRecord genus in genera.Values)
        {
            genus.plants = 0;
        }
        for (int i = 0; i < plantGenomes.Count; i++)
        {
            if (genera.TryGetValue(plantGenomes[i].genusId, out GenusRecord genus))
            {
                genus.plants++;
            }
        }
        RemoveExtinct();
    }

    internal void RemoveExtinct()
    {
        extinctIds.Clear();
        foreach (GenusRecord genus in genera.Values)
        {
            if (genus.plants + genus.seeds <= 0)
            {
                extinctIds.Add(genus.id);
            }
        }
        for (int i = 0; i < extinctIds.Count; i++)
        {
            genera.Remove(extinctIds[i]);
            ResearchCounters.Today.generaExtinct++;
        }
    }

    /// <summary>
    /// Checks a newly made seed genome once, at dispersal. It stays in its genus if it could breed with it, otherwise
    /// joins the closest genus of its kind it could breed with; failing both it stays with its own (or its mother's)
    /// genus: a lone seed never founds a genus, though the plant it grows into may at a later grouping.
    /// Seeds of one dispersal share their genome, so this runs once per dispersal. Does not count the seeds: the caller
    /// adds each seed it actually places with AddSeed.
    /// </summary>
    internal GenusRecord ClassifySeed(PlantGenome genome, PlantGenome mother, float maxBreedingDistance, long day)
    {
        if (FitsOwnGenus(genome, maxBreedingDistance, out GenusRecord genus))
            return genus;

        bool hasOwn = genus != null;
        GenusRecord motherGenus = null;
        if (mother != null)
        {
            genera.TryGetValue(mother.genusId, out motherGenus);
        }
        bool fromLibrary = hasOwn ? genus.isLibrary : motherGenus != null && motherGenus.isLibrary;
        GenusRecord closest = FindClosestGenus(genome, maxBreedingDistance, fromLibrary);
        if (closest != null)
        {
            WriteGenus(genome, closest, null);
            return closest;
        }
        if (hasOwn)
            return genus;
        if (motherGenus != null)
        {
            WriteGenus(genome, motherGenus, null);
            return motherGenus;
        }

        // Only reached when no genus records it (e.g. a save without genera); seeds keep a genus alive, so found one
        GenusRecord founded = Found(genome, PlantGenome.NO_GENUS, day);
        WriteGenus(genome, founded, null);
        return founded;
    }

    internal void AddSeed(GenusRecord genus)
    {
        genus.seeds++;
    }

    /// <summary>
    /// A seed died or sprouted. genusId is the genus it was counted in (Seed.genusId), which may differ from its
    /// genome's current genus if a sibling's sprouting moved their shared genome.
    /// </summary>
    internal void RemoveSeed(int genusId)
    {
        if (genera.TryGetValue(genusId, out GenusRecord genus) && genus.seeds > 0)
        {
            genus.seeds--;
        }
    }

    internal void MoveSeed(int fromGenusId, GenusRecord to)
    {
        RemoveSeed(fromGenusId);
        to.seeds++;
    }

    internal void ClearSeedCounts()
    {
        foreach (GenusRecord genus in genera.Values)
        {
            genus.seeds = 0;
        }
    }

    /// <summary>
    /// The second check a seed gets: when it sprouts. The seedling stays in its genus if it could breed with it,
    /// otherwise joins the closest genus of its kind it could breed with; failing both it stays put until the next
    /// grouping, where it may found a genus with others like it. Counted as a plant either way.
    /// </summary>
    internal void PlaceSprout(PlantGenome genome, float maxBreedingDistance, long day)
    {
        if (FitsOwnGenus(genome, maxBreedingDistance, out GenusRecord genus))
        {
            genus.plants++;
            return;
        }

        GenusRecord closest = FindClosestGenus(genome, maxBreedingDistance, genus != null && genus.isLibrary);
        if (closest != null)
        {
            Join(genome, closest, null);
        }
        else if (genus != null)
        {
            genus.plants++;
        }
        else
        {
            Assign(genome, maxBreedingDistance, day);
        }
    }

    private bool FitsOwnGenus(PlantGenome genome, float maxBreedingDistance, out GenusRecord genus)
    {
        return genera.TryGetValue(genome.genusId, out genus) && DistanceToGenus(genome, genus) < maxBreedingDistance;
    }

    private static float DistanceToGenus(PlantGenome genome, GenusRecord genus)
    {
        float closest = float.MaxValue;
        for (int i = 0; i < REPRESENTATIVES; i++)
        {
            if (genus.representatives[i] != null)
            {
                closest = Mathf.Min(closest, GeneManager.GeneticDistance(genome, genus.representatives[i]));
            }
        }
        return closest;
    }

    // Reservoir sampling: each candidate ends up in each slot with equal chance, without storing the candidates
    private static void ConsiderAsRepresentative(GenusRecord genus, PlantGenome genome)
    {
        genus.candidatesSeen++;
        if (genus.candidatesSeen <= REPRESENTATIVES)
        {
            genus.nextRepresentatives[genus.candidatesSeen - 1] = genome;
            return;
        }
        int slot = Random.Range(0, genus.candidatesSeen);
        if (slot < REPRESENTATIVES)
        {
            genus.nextRepresentatives[slot] = genome;
        }
    }

    /// <summary>
    /// Outliers first join any genus of their own kind (library or generated) they can breed with. The rest are
    /// grouped by who could breed with whom; a large enough group founds a new generated genus, and smaller ones
    /// stay where they were.
    /// </summary>
    private void PlaceOutliers(float maxBreedingDistance, long day)
    {
        if (outliers.Count == 0)
            return;

        unplaced.Clear();
        for (int i = 0; i < outliers.Count; i++)
        {
            PlantGenome genome = outliers[i];
            bool fromLibrary = genera.TryGetValue(genome.genusId, out GenusRecord currentGenus) && currentGenus.isLibrary;
            GenusRecord closest = FindClosestGenus(genome, maxBreedingDistance, fromLibrary);
            if (closest != null)
            {
                Join(genome, closest, null);
            }
            else
            {
                unplaced.Add(genome);
            }
        }

        // Each pass takes the first remaining outlier and everyone who could breed with it
        while (unplaced.Count > 0)
        {
            PlantGenome groupFounder = unplaced[0];
            group.Clear();
            for (int i = unplaced.Count - 1; i >= 0; i--)
            {
                if (GeneManager.GeneticDistance(unplaced[i], groupFounder) < maxBreedingDistance)
                {
                    group.Add(unplaced[i]);
                    unplaced.RemoveAt(i);
                }
            }

            bool hasHome = genera.TryGetValue(groupFounder.genusId, out GenusRecord home);
            if (group.Count >= MIN_SPLIT_GROUP || !hasHome)
            {
                home = Found(groupFounder, groupFounder.genusId, day);
            }
            for (int i = 0; i < group.Count; i++)
            {
                Join(group[i], home, null);
            }
        }
    }

    /// <summary>
    /// Writes a genus and species onto a plant's genome and counts the plant.
    /// </summary>
    private void Join(PlantGenome genome, GenusRecord genus, SpeciesRecord species)
    {
        WriteGenus(genome, genus, species);
        genus.plants++;
    }

    /// <summary>
    /// Writes a genus and species onto a genome. With no species given, a member keeps its species if the genus
    /// has it, and otherwise takes the genus's first.
    /// </summary>
    private void WriteGenus(PlantGenome genome, GenusRecord genus, SpeciesRecord species)
    {
        if (species == null && genome.genusId == genus.id)
        {
            species = genus.FindSpecies(genome.species);
        }
        if (species == null)
        {
            species = genus.species.Count > 0 ? genus.species[0] : AddNextSpecies(genus);
        }

        genome.genusId = genus.id;
        genome.genusColor = genus.founderColor;
        genome.genus = genus.name;
        genome.species = species.name;
    }

    private GenusRecord FindClosestGenus(PlantGenome genome, float maxBreedingDistance, bool library)
    {
        GenusRecord closest = null;
        float closestDistance = maxBreedingDistance;
        foreach (GenusRecord genus in genera.Values)
        {
            if (genus.isLibrary != library)
                continue;
            float distance = DistanceToGenus(genome, genus);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = genus;
            }
        }
        return closest;
    }

    private GenusRecord FindLibraryGenus(string genusName)
    {
        foreach (GenusRecord genus in genera.Values)
        {
            if (genus.isLibrary && string.Equals(genus.name, genusName, System.StringComparison.OrdinalIgnoreCase))
                return genus;
        }
        return null;
    }

    private string NextGenusName()
    {
        while (true)
        {
            string name = BinomialNames.GenusName(nextGenusNameIndex++);
            if (!IsGenusNameTaken(name))
                return name;
        }
    }

    private bool IsGenusNameTaken(string name)
    {
        foreach (GenusRecord genus in genera.Values)
        {
            if (string.Equals(genus.name, name, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates a genus founded by this genome. A library name makes it a library genus (the caller adds the species);
    /// otherwise it takes the next generated names.
    /// </summary>
    private GenusRecord Found(PlantGenome founder, int parentGenusId, long day, string libraryName = null)
    {
        GenusRecord genus = new GenusRecord
        {
            id = nextGenusId++,
            name = libraryName ?? NextGenusName(),
            isLibrary = libraryName != null,
            parentGenusId = genera.ContainsKey(parentGenusId) ? parentGenusId : PlantGenome.NO_GENUS,
            foundedDay = day,
            founderColor = new Color(
                founder.GetTraitValue(TraitId.ColorR),
                founder.GetTraitValue(TraitId.ColorG),
                founder.GetTraitValue(TraitId.ColorB),
                1f)
        };
        genus.representatives[0] = founder;
        genera[genus.id] = genus;
        if (libraryName == null)
        {
            AddNextSpecies(genus);
        }
        ResearchCounters.Today.generaFounded++;
        return genus;
    }

    // ---- Save/load ----

    internal void Clear()
    {
        genera.Clear();
        nextGenusId = 1;
        nextGenusNameIndex = 1;
    }

    internal void Restore(GenusRecord genus)
    {
        genera[genus.id] = genus;
    }

    internal void RestoreCounters(int nextId, int nextNameIndex)
    {
        nextGenusId = Mathf.Max(nextId, 1);
        nextGenusNameIndex = Mathf.Max(nextNameIndex, 1);
    }
}
