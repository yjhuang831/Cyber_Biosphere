using UnityEngine;

public struct Seed
{
    public PlantGenome carriedGenome;
    public string parentGenus;
    public float storedEnergy;
    public float phosphorus;        // From the parent's flowering; goes to the seedling, or to the tile if the seed dies
    public float germinationEnergy;
    public byte germinationStage;
    public byte sproutStage;
    public float germinationCue;    // Germination only progresses when habitat suitability >= this (0 = always)
    public float coldPreservation;  // Fraction of daily decay avoided at full cold (0 = none)
}
