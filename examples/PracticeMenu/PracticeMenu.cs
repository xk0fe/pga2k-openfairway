using System;
using System.Linq;
using OpenFairway.API;

namespace Examples;

/// <summary>Rebuilds the main menu's HOME tab: two of the game's own tiles, a tile that starts nine holes on a
/// random course, and one that opens a choice popup.</summary>
public sealed class PracticeMenu : IFairwayMod
{
    private IModContext _ctx = null!;

    public string Id => "example.practicemenu";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        ctx.Ui.MainMenu.SetTabs(MenuTab.Home, MenuTab.Design);
        // HOME fills its slots top-left, bottom-left, top-right, bottom-right.
        ctx.Ui.MainMenu.SetTiles(MenuTab.Home,
            MenuTile.Game(GameTile.QuickPlay),
            MenuTile.Game(GameTile.Training),
            new MenuTile { Label = "QUICK NINE", Description = "Nine holes on a random course.", OnClick = QuickNine },
            new MenuTile { Label = "TIPS", Description = "Something to work on today.", OnClick = Tips });
        // Any text on screen can be rewritten; '*' matches any run of characters.
        ctx.Ui.ReplaceText("OFFLINE*Reconnect*", "Practice mode");
    }

    private void QuickNine()
    {
        var courses = _ctx.Ui.Courses.All.Where(c => c.IsRealCourse && c.Holes == 18).ToList();
        if (courses.Count == 0) return;
        var course = courses[new Random().Next(courses.Count)];
        if (!_ctx.Ui.Courses.OpenMatch(course.Id, MatchHoles.FrontNine))
            _ctx.Ui.ShowNotice("QUICK NINE", $"{course.Name} could not be opened.");
    }

    private void Tips() => _ctx.Ui.ShowChoices("TIPS", "Pick something to practise.", new[]
    {
        new MenuChoice { Label = "PUTTING", Description = "Lag putts from 30 feet.", OnSelect = () => _ctx.Ui.ShowNotice("PUTTING", "Ten putts from 30 feet. Count how many stop inside three feet.") },
        new MenuChoice { Label = "WEDGES", Description = "Hit three distances.", OnSelect = () => _ctx.Ui.ShowNotice("WEDGES", "Hit 50, 75 and 100 yards, five balls each.") },
    });
}
