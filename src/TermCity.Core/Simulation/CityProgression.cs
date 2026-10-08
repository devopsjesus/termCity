using TermCity.Core.Buildings;

namespace TermCity.Core.Simulation;

public sealed record CityMilestone(int Population, string Name, string Advice);

/// <summary>Permanent city-size achievements, independent of the optional guide.</summary>
public static class CityProgression
{
    public static IReadOnlyList<CityMilestone> Milestones { get; } = Array.AsReadOnly(new[]
    {
        new CityMilestone(100, "Hamlet", "Keep jobs growing with homes; add local care as the weekly surplus allows."),
        new CityMilestone(500, "Village", "Burgage houses can redevelop with a motte, road access and good land value. Spread care around homes."),
        new CityMilestone(1_000, "Market Town", "Expand fuel and water before zoning another district; retain a winter reserve."),
        new CityMilestone(2_500, "Borough", "A castle anchors the town. Check traffic and upgrade the busiest tracks."),
        new CityMilestone(5_000, "City", "Build a cathedral only when its upkeep is affordable; maintain jobs and services."),
        new CityMilestone(10_000, "Great City", "All city-size gates are open! Continue expanding at a sustainable weekly surplus."),
    });

    public static int RequiredPopulation(BuildingType building) =>
        building.MinPopulation <= 0 ? 0 :
            Milestones.FirstOrDefault(m => m.Population >= building.MinPopulation)?.Population ?? building.MinPopulation;

    public static bool IsUnlocked(CityGame game, BuildingType building) =>
        building.MinPopulation <= 0 || Math.Max(game.HighestMilestone, game.Stats.Population) >= RequiredPopulation(building);

    public static string Unlocks(CityGame game, CityMilestone milestone)
    {
        var names = game.Map.Content.Buildings
            .Where(b => b.PlayerPlaceable && b.MinPopulation > 0 && RequiredPopulation(b) == milestone.Population)
            .Select(b => b.Name).ToList();
        if (milestone.Population == 500) names.Add("burgage-house redevelopment (with a lord's seat and land value)");
        if (milestone.Population == 5_000) names.Add("tenement redevelopment (with a keep, cobbles and land value)");
        return names.Count == 0 ? "All earlier buildings remain available." : "Now available: " + string.Join(", ", names) + ".";
    }

    public static string Announcement(CityGame game, IEnumerable<CityMilestone> milestones) =>
        string.Join("\n\n", milestones.Select(m =>
            $"City Grew! {m.Name} - {m.Population:N0} souls\n{Unlocks(game, m)}\n{m.Advice}"));

    public static string NextStep(CityGame game)
    {
        if (game.Stats.Residential.Zoned == 0)
            return "1. Find an edge-connected road. Zone 20-30 homes (R); leave open plots for services. F6: guide.";
        if (GrowthDiagnostics.ForZone(game, World.ZoneType.Residential).EligibleVacancies == 0 &&
            game.Stats.Residential.Occupied == 0)
            return "2. Connect homes to the King's Road or map edge with a short track (T). F8 checks access.";
        if (game.Config.FullRules && game.Services.ActiveCounts[(int)ServiceKind.Power] == 0)
            return "3. Build a Woodlot (8,000g) by a connected road: Enter > Services. Homes need fuel.";
        if (game.Config.FullRules && game.Services.ActiveCounts[(int)ServiceKind.Water] == 0)
            return "4. Build a Town Well (4,000g) by a connected road. Save gold for roads and winter.";
        if (game.Config.FullRules && (game.Services.Power.Ratio < 1 || game.Services.Water.Ratio < 1))
            return "Supplies are short! Add connected fuel/water before homes. Rebuild lost supplies with reserve gold.";
        if (game.Stats.Residential.Occupied < game.Config.MinResidentialCells)
            return $"5. {(game.Paused ? "Resume (P)" : "Let families settle")}: reach {game.Config.MinResidentialCells} occupied homes, then zone jobs (C/I).";
        if (game.Stats.Commercial.Zoned == 0 || game.Stats.Industrial.Zoned == 0 || game.Indicators.Unemployment > 0.12)
            return "6. Zone shops (C) and workshops (I) by roads. Keep smoky workshops away from homes.";
        if (game.Config.FullRules && game.Finance.Net <= 0)
            return "Balance the budget before more services. Try 75% local funding; save gold for grain and tribute.";
        if (game.Config.FullRules && game.Stats.Population >= 100 && game.Indicators.Happiness < 55 &&
            game.Indicators.Complaints.FirstOrDefault() is { } complaint)
            return complaint.Reason == "Smoke"
                ? "Smoke holds growth back. Move workshops away from homes; greens help. Keep fuel sources clean."
                : $"Care: {complaint.Reason} holds growth back. Improve nearby cover; save for it. F6: Happiness.";
        if (game.Stats.Residential.Zoned - game.Stats.Residential.Filled < 20)
            return "Expand a short connected track and zone homes. Leave service plots; do not spend your reserve.";
        if (game.Config.FullRules && game.Services.SeatRank == 0)
            return "7. Save for a Motte and Bailey (14,000g): settlers, defence and denser homes at 500 souls.";
        var next = Milestones.FirstOrDefault(m => m.Population > game.HighestMilestone);
        return next is null
            ? "Great City achieved! Keep supplies, jobs and local services in step. F7 reviews all unlocks."
            : $"Next: {next.Name}, {next.Population:N0} souls. Expand homes, jobs, supplies and care. F7: unlocks.";
    }
}
