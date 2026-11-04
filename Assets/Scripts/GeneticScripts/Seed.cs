using UnityEngine;

public struct Seed
{
    public PlantGenome carriedGenome;
    public float storedEnergy;
    public int genusId;             // Genus this seed is counted in (GenusTracker seeds); set at dispersal and on load
    public float phosphorus;        // From the parent's flowering; goes to the seedling, or to the tile if the seed dies
    public float germinationEnergy;
    public byte germinationStage;
    public byte sproutStage;
    public float germinationCue;    // Germination only progresses when habitat suitability >= this (never below SeedManager.minimumSproutFit)
    public float coldPreservation;  // Fraction of daily decay avoided at full cold (0 = none)
}
