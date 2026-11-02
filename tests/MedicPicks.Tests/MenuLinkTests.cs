using System.Text;
using Jellyfin.Plugin.MedicPicks.Web;

namespace MedicPicks.Tests;

/// <summary>The script tag that opens My picks inside the web app, added to the web app's page as it's served.</summary>
public class MenuLinkTests
{
    private static string AddScript(string html) =>
        Encoding.UTF8.GetString(MenuLinkMiddleware.AddScript(Encoding.UTF8.GetBytes(html), "../MedicPicks/InApp.js"));

    [Fact]
    public void Adds_the_script_before_the_closing_body_tag()
    {
        string page = AddScript("<html><body><div id=\"reactRoot\"></div><script src=\"main.js\"></script></body></html>");

        Assert.Equal(
            "<html><body><div id=\"reactRoot\"></div><script src=\"main.js\"></script>"
            + "<script id=\"medic-picks-inapp\" src=\"../MedicPicks/InApp.js\" defer></script></body></html>",
            page);
    }

    [Fact]
    public void Adds_it_once()
    {
        string once = AddScript("<html><body></body></html>");

        Assert.Equal(once, AddScript(once));
    }

    [Fact]
    public void Leaves_a_page_without_a_body_alone()
    {
        Assert.Equal("<p>not the web app</p>", AddScript("<p>not the web app</p>"));
    }

    [Fact]
    public void Uses_the_last_closing_body_tag()
    {
        string page = AddScript("<body><template></body></template></BODY>");

        Assert.EndsWith("defer></script></BODY>", page, StringComparison.Ordinal);
        Assert.StartsWith("<body><template></body></template>", page, StringComparison.Ordinal);
    }
}
