using TermCity.Core.Rendering;
using TermCity.Core.Session;

namespace TermCity.GodotApp;

public sealed record ReportSection(string Title, IReadOnlyList<SidebarCell[]> Rows);

public static class ReportTables
{
    public static IReadOnlyList<ReportSection> Build(GameSession session)
    {
        var pages = ReportContent.Build(session.Game);
        int tab = session.Prompt?.Title == "Weekly Report" ? session.Prompt.ActiveTab : 0;
        return pages[tab].Groups.Select(group => new ReportSection(group.Title,
            group.Rows.Select(row => row.Select(cell => new SidebarCell(cell.Text, cell.Color)).ToArray()).ToArray())).ToArray();
    }
}
