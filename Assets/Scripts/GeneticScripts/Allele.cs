using UnityEngine;

public struct Allele
{
    public byte value;
    public bool dominance;
    public int traitId;

    public Allele(byte val, bool dom, int traitId)
    {
        this.value = val;
        this.dominance = dom;
        this.traitId = traitId;
    }
}
