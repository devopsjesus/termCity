using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public sealed record PeasantNeed(string Reason, string Text, Rgb Color, string Hint);

public static class PeasantNeeds
{
    public static PeasantNeed? Describe(CityIndicators indicators, int variation, int complaintIndex = 0)
    {
        var complaints = indicators.Complaints.OrderByDescending(c => c.Points).ToArray();
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
            "Tithes" => "lighter taxes", "Road access" => "roads to their homes",
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
            "No fuel" => "Build a road-connected Woodlot or Charcoal Burners. Check Fuel supply against demand.",
            "No water" => "Build a road-connected Town Well or a shore-side Aqueduct. Check Water supply against demand.",
            "Smoke" => "Move smoky industry and Charcoal Burners away from homes; Village Greens reduce nearby pollution.",
            "Lawlessness" => "Place a Watch House, Sheriff's Hall or Gaol near affected homes and check service funding.",
            "Fire Watch" => "Place a road-connected Fire Watch near homes and maintain its funding.",
            "Physic" => "Place an Apothecary, Hospice or Infirmary near homes and maintain its funding.",
            "Learning" => "Place a Chantry School or Monastery near homes and maintain its funding.",
            "Commons" => "Place a Village Green or Tavern near homes when its milestone unlocks.",
            "Unguarded" => "Build a road-connected Motte and Bailey, Stone Keep or Castle near homes.",
            "Solace" => "Place a Chapel, Parish Church or Cathedral near homes.",
            "Hunger" => "Keep gold for automatic grain imports, add Market Cross coverage, and use connected Granaries to store harvest surplus. See Food & Seasons.",
            "Cart traffic" => "Upgrade busy roads and improve routes between homes and workplaces. Check Growth and Services reports.",
            "Idleness" => "Provide road-served commercial and industrial workplaces with fuel and water; inspect Growth for blockers.",
            "Tithes" => "Reduce residential taxes in the economy menu, while keeping enough income for upkeep.",
            "Road access" => "Connect residential plots to an edge-connected road within road-service reach.",
            _ => "Open the Services report to inspect complaints, coverage and funding.",
        };
        return new(complaint.Reason, text, Rgb.Hex(dire ? 0xff7777 : 0xffe066), hint);
    }
}
