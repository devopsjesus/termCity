using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public sealed record PeasantNeed(string Reason, string Text, Rgb Color, string Hint);

public static class PeasantNeeds
{
    public static PeasantNeed? Describe(CityGame game, int variation, int complaintIndex = 0)
    {
        int population = game.Stats.Population;
        var indicators = game.Indicators with
        {
            Complaints = game.Indicators.Complaints.Where(c => (c.Reason != "Hunger" || game.GrainRequired) &&
                CityProgression.IsServiceUnlocked(game, ServiceForComplaint(c.Reason), population)).ToArray(),
        };
        var message = Describe(indicators, variation, complaintIndex);
        if (message is null) return null;
        ServiceKind kind = ServiceForComplaint(message.Reason);
        if (kind == ServiceKind.None) return message;
        string name = CityReport.ServiceName(kind).ToLowerInvariant();
        double load = game.Services.Workload(kind);
        bool waiting = game.Map.ZoneCells(ZoneType.Residential).Any(i => game.Map.IsBuildingAnchor(i) &&
            game.Map.HouseholdAt(i % game.Map.Width, i / game.Map.Width).Total > 0 && !game.Services.HasArrived(kind, i));
        string text = game.Services.BuildingCounts[(int)kind] == 0 ? $"Sire, {name} is now available but has not been dispatched." :
            load >= 1 ? $"Sire, {name} is at capacity; arrivals are delayed." :
            load >= 0.85 ? $"Sire, {name} is nearing its limit; queues are growing." :
            waiting ? $"Sire, the people are still waiting for {name} to arrive." :
            $"Sire, {name} is taking too long to reach the people.";
        return message with
        {
            Text = text,
            Hint = $"Check {name} travel times and workload in Building Status and Services. " +
                "Roads speed travel; nearer buildings and funding increase timely service and capacity.",
        };
    }

    private static ServiceKind ServiceForComplaint(string reason) => reason switch
    {
        "No fuel" => ServiceKind.Power, "No water" => ServiceKind.Water,
        "Fire Watch" => ServiceKind.Fire, "Lawlessness" => ServiceKind.Police,
        "Physic" => ServiceKind.Health, "Learning" => ServiceKind.Education,
        "Commons" => ServiceKind.Recreation, "Unguarded" => ServiceKind.Defence,
        "Solace" => ServiceKind.Faith, _ => ServiceKind.None,
    };

    public static PeasantNeed? Describe(CityIndicators indicators, int variation, int complaintIndex = 0)
    {
        var complaints = indicators.Complaints
            .Where(c => c.Reason is not ("Slow journeys" or "Road access"))
            .OrderByDescending(c => c.Points).ToArray();
        if (complaints.Length == 0) return null;
        var complaint = complaints[Math.Abs((long)complaintIndex) % complaints.Length];
        string need = complaint.Reason switch
        {
            "No fuel" => "fuel for their hearths", "No water" => "clean water",
            "Smoke" => "cleaner air", "Lawlessness" => "a sheriff's protection",
            "Fire Watch" => "a fire watch", "Physic" => "healers", "Learning" => "schooling",
            "Commons" => "common ground", "Unguarded" => "a lord's protection",
            "Solace" => "a place of worship", "Hunger" => "bread and grain",
            "Cart traffic" => "clearer roads", "Idleness" => "work",
            "Tithes" => "lighter taxes",
            _ => $"relief from {complaint.Reason.ToLowerInvariant()}",
        };
        string text = (variation % 4) switch
        {
            0 => $"The peasants demand {need}, sire.",
            1 => $"Your people plead for {need}, my lord.",
            2 => $"Sire, the town is crying out for {need}.",
            _ => $"A petition arrives: the people need {need}.",
        };
        bool dire = complaint.Points >= 15 ||
            complaint.Reason is "Hunger" or "No fuel" or "No water" && complaint.Points >= 5 ||
            indicators.Happiness < 35;
        string hint = complaint.Reason switch
        {
            "No fuel" => "Build a nearby Woodlot or Charcoal Burners. Check Fuel deliveries and capacity.",
            "No water" => "Build a nearby Town Well or a shore-side Aqueduct. Check Water deliveries and capacity.",
            "Smoke" => "Move smoky industry and Charcoal Burners away from homes; Village Greens reduce nearby pollution.",
            "Lawlessness" => "Place a Watch House, Sheriff's Hall or Gaol near affected homes and check service funding.",
            "Fire Watch" => "Shorten Fire Watch journeys with roads and maintain its staffing and funding.",
            "Physic" => "Place an Apothecary, Hospice or Infirmary near homes and maintain its funding.",
            "Learning" => "Place a Chantry School or Monastery near homes and maintain its funding.",
            "Commons" => "Place a Village Green or Tavern near homes when its milestone unlocks.",
            "Unguarded" => "Build a nearby Motte and Bailey, Stone Keep or Castle and shorten patrol journeys.",
            "Solace" => "Place a Chapel, Parish Church or Cathedral near homes.",
            "Hunger" => "Keep gold for automatic grain imports, add Market Cross coverage, and use connected Granaries to store harvest surplus. See Food & Seasons.",
            "Cart traffic" => "Upgrade busy roads and improve routes between homes and workplaces. Check Growth and Services reports.",
            "Idleness" => "Provide commercial and industrial workplaces with timely fuel and water deliveries; inspect Growth.",
            "Tithes" => "Reduce residential taxes in the economy menu, while keeping enough income for upkeep.",
            _ => "Open the Services report to inspect complaints, coverage and funding.",
        };
        return new(complaint.Reason, text, Rgb.Hex(dire ? 0xff7777 : 0xffe066), hint);
    }
}
