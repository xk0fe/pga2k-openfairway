using System.Linq;
using OpenFairway.API;

namespace Examples;

/// <summary>A full custom screen: the game's courses as photo cards, eight to a page. Selecting one starts nine
/// holes on it. Shows drawing on the canvas, entrance animation, buttons, paging and starting a match.</summary>
public sealed class CourseBrowser : IFairwayMod
{
    // Colours are 0xRRGGBBAA. These are the game's own menu colours (see docs/design.md).
    private const uint Bg = 0x0F0F1DFFu, Tile = 0x242731FFu, TileHover = 0x2E323DFFu, Text = 0xF4F4F4FFu, Muted = 0xAEB4C6FFu;
    private const int PerPage = 8;

    private IModContext _ctx = null!;
    private IUiScreen _screen = null!;
    private int _page;

    public string Id => "example.coursebrowser";

    public void OnLoad(IModContext ctx)
    {
        _ctx = ctx;
        _screen = ctx.Ui.CreateScreen();
        _screen.Render = Draw;           // called on open, after Invalidate, and when the window is resized
        _screen.OnBack = _screen.Close;  // Escape or B
        ctx.Actions.Register("coursebrowser.open", Open);
        ctx.Ui.MainMenu.SetTiles(MenuTab.Home,
            new MenuTile { Label = "COURSES", Description = "Every course, offline.", OnClick = Open },
            MenuTile.Game(GameTile.QuickPlay));
    }

    private void Open()
    {
        _page = 0;
        _screen.Open();
    }

    private void Draw(IUiCanvas c)
    {
        var courses = _ctx.Ui.Courses.All.OrderBy(course => course.Name).ToList();
        var pages = (courses.Count + PerPage - 1) / PerPage;

        // The canvas is always 1920x1080; the platform scales it to the window.
        c.Rect(0, 0, 1920, 1080, Bg);
        c.Label(80, 56, 1200, 70, "COURSES", 56, Text, UiAlign.Left, heading: true);
        c.Label(84, 126, 1200, 34, $"{courses.Count} courses  ·  page {_page + 1} of {pages}", 22, Muted);

        for (var i = 0; i < PerPage && _page * PerPage + i < courses.Count; i++)
        {
            var course = courses[_page * PerPage + i];
            float x = 80 + i % 4 * 450, y = 200 + i / 4 * 380;
            // Everything drawn inside a group fades and rises in together, here one card after another.
            using (c.Group(delay: i * 0.04f, rise: 14))
            {
                c.Clickable(x, y, 430, 360, () => Play(course), Tile, TileHover, radius: 4);
                if (_ctx.Ui.Courses.Thumbnail(course.Id) is { } photo) c.Image(x, y, 430, 250, photo, radius: 4);
                c.Label(x + 20, y + 262, 390, 40, course.Name.ToUpperInvariant(), course.Name.Length > 24 ? 18 : 22, Text, UiAlign.Left, heading: true);
                c.Label(x + 20, y + 304, 390, 30, $"Par {course.Par}  ·  {course.Holes} holes", 18, Muted);
            }
        }

        c.Button(80, 980, 220, 60, "PREVIOUS", () => Turn(-1, pages), Tile, TileHover, Text, 22, radius: 4);
        c.Button(320, 980, 220, 60, "NEXT", () => Turn(1, pages), Tile, TileHover, Text, 22, radius: 4);
    }

    private void Turn(int by, int pages)
    {
        _page = (_page + by + pages) % pages;
        _screen.Invalidate(animate: true); // redraw, playing the entrance again
    }

    private void Play(CourseInfo course)
    {
        _screen.Close(); // matches start from the main menu
        if (!_ctx.Ui.Courses.OpenMatch(course.Id, MatchHoles.FrontNine))
            _ctx.Ui.ShowNotice("COURSES", $"{course.Name} could not be opened.");
    }
}
