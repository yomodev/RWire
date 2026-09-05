namespace RWire;

/// <summary>
/// Wire type tag for an encoded R value (docs/spec.md section 5).
/// </summary>
public enum RTypeTag : byte
{
    Null = 0,
    Logical = 1,
    Integer = 2,
    Double = 3,
    Character = 4,
    Raw = 5,
    List = 6,
    Table = 7,
}
