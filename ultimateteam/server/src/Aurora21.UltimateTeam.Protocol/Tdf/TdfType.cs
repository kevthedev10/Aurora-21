namespace Aurora21.UltimateTeam.Protocol.Tdf;

/// <summary>Blaze Heat2 TDF wire types.</summary>
public enum TdfType : byte
{
    Integer = 0x0,
    String = 0x1,
    Blob = 0x2,
    Struct = 0x3,
    List = 0x4,
    Map = 0x5,
    Union = 0x6,
    Variable = 0x7,
    ObjectType = 0x8,
    ObjectId = 0x9,
    Float = 0xA,

    /// <summary>Only a list or map element type; a TimeValue member is sent as <see cref="Integer"/>.</summary>
    TimeValue = 0xB,

    /// <summary>A self-describing value: present byte, TDF type id, the value without a member header, then 0x00.</summary>
    Generic = 0xC,
}
