using OpenFairway.API;

namespace Examples;

/// <summary>The smallest useful mod: restyles the main menu's banner and shows a popup when it is selected.</summary>
public sealed class HelloBanner : IFairwayMod
{
    public string Id => "example.hellobanner";

    public void OnLoad(IModContext ctx)
    {
        ctx.Ui.MainMenu.SetBanner(new MenuBanner
        {
            Title = "HELLO",
            Subtitle = "MY FIRST MOD",
            CoursePhoto = true,          // one of the game's course photos behind the title
            CalloutLabel = "SAY HI",     // the prompt in the banner's corner
            OnClick = () => ctx.Ui.ShowNotice("HELLO", "This popup comes from a mod."),
        });
        ctx.Log.Info("Hello from a mod");
    }
}
