using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.Tests;

public class BuildingReportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GranaryStatusWorksFromAnyFootprintCellAndShowsActualStorage(bool connected)
    {
        var game = TestCity.Flat(rules: CityRules.Full);
        var granary = game.Map.Content.Buildings.Get("Granary");
        var position = connected ? new Pos(80, 18) : new Pos(30, 5);
        Assert.True(game.PlaceBuilding(granary, new(position.X, position.Y, 1, 1)).Success);
        var secondCell = position.Offset(1, 0);
        var session = new GameSession(game);
        session.ShowBuildingStatus(secondCell);
        Assert.Equal(BuildingReport.Title, session.Prompt!.Title);
        Assert.True(session.ClockSuspended);
        Assert.Contains(connected ? "Active" : "Disconnected", session.Prompt.Text);
        var report = BuildingReport.Build(game, secondCell);
        var rows = report.Groups[0].Rows;
        Assert.Equal(connected ? "4,000 grain measures" : "0 grain measures",
            rows.Single(row => row[0].Text == "Active storage")[1].Text);
        Assert.Contains("City grain reserve", session.Prompt.Text);
        Assert.Contains("Import capacity", session.Prompt.Text);
        Assert.Contains("do not produce grain", session.Prompt.Text);
        session.SelectPrompt(1);
        Assert.Null(session.Prompt);
        Assert.False(game.Paused);
    }
}
