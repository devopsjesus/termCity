namespace TermCity.Core.Buildings;

/// <summary>What a stand-alone civic building provides. Utilities are city-wide supply; the rest cover an area.</summary>
public enum ServiceKind : byte
{
    None = 0,
    Power = 1,
    Water = 2,
    Fire = 3,
    Police = 4,
    Health = 5,
    Education = 6,
    Recreation = 7,
}

public static class ServiceKinds
{
    /// <summary>Services that cover an area around a building (as opposed to city-wide utilities).</summary>
    public static readonly IReadOnlyList<ServiceKind> Area =
        [ServiceKind.Fire, ServiceKind.Police, ServiceKind.Health, ServiceKind.Education, ServiceKind.Recreation];

    public static readonly IReadOnlyList<ServiceKind> Utilities = [ServiceKind.Power, ServiceKind.Water];

    public static readonly IReadOnlyList<ServiceKind> All =
        [ServiceKind.Power, ServiceKind.Water, ServiceKind.Fire, ServiceKind.Police,
         ServiceKind.Health, ServiceKind.Education, ServiceKind.Recreation];

    public static bool IsUtility(this ServiceKind kind) => kind is ServiceKind.Power or ServiceKind.Water;
}
