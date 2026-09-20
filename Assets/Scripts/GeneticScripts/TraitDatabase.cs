using UnityEngine;

public class TraitDatabase : MonoBehaviour
{
    public TraitDefinition[] traits; // assign in inspector (order defines trait indices)
    public static TraitDatabase I { get; private set; }
    void Awake() => I = this;
}
