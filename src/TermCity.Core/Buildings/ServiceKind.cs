namespace TermCity.Core.Buildings;

/// <summary>
/// What a stand-alone civic building provides. Fuel, water and grain stores are city-wide supply; the rest cover an area.
/// Power is fuel (firewood and charcoal), Police is the sheriff and watch, Health is physic, Education is learning,
/// Recreation is the commons; Defence is the lord's garrison (castles), Faith is chapels and churches, Trade is markets and guilds.
/// </summary>
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
    Defence = 8,
    Faith = 9,
    Trade = 10,
    Granary = 11,
}

public static class ServiceKinds
{
    /// <summary>The number of <see cref="ServiceKind"/> values: the size of every array indexed by one.</summary>
    public const int Count = 12;

    /// <summary>Services that cover an area around a building (as opposed to city-wide supplies).</summary>
    public static readonly IReadOnlyList<ServiceKind> Area =
    [
        ServiceKind.Fire, ServiceKind.Police, ServiceKind.Health, ServiceKind.Education, ServiceKind.Recreation,
        ServiceKind.Defence, ServiceKind.Faith, ServiceKind.Trade,
    ];

    /// <summary>City-wide supplies: they are always paid in full and are not covered by the budget.</summary>
    public static readonly IReadOnlyList<ServiceKind> Utilities = [ServiceKind.Power, ServiceKind.Water, ServiceKind.Granary];

    public static readonly IReadOnlyList<ServiceKind> All =
    [
        ServiceKind.Power, ServiceKind.Water, ServiceKind.Fire, ServiceKind.Police, ServiceKind.Health,
        ServiceKind.Education, ServiceKind.Recreation, ServiceKind.Defence, ServiceKind.Faith, ServiceKind.Trade,
        ServiceKind.Granary,
    ];

    public static bool IsUtility(this ServiceKind kind) => kind is ServiceKind.Power or ServiceKind.Water or ServiceKind.Granary;
}
