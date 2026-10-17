using System.Reflection;
using Jellyfin.Plugin.MedicPicks.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace MedicPicks.Tests;

/// <summary>
/// Picks has routes for every signed-in user and routes for admins in one controller, so each action says who may
/// call it. An action that forgot would be open to every account; these keep the routes and the rules together.
/// </summary>
public class PicksAccessTests
{
    private static readonly string Prefix = typeof(PicksController).GetCustomAttribute<RouteAttribute>()!.Template;

    // Every public method is an action unless marked [NonAction]; one with no template of its own answers at the
    // controller's route, so it counts too.
    private static IEnumerable<(MethodInfo Action, string Route)> Actions() =>
        typeof(PicksController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
            .SelectMany(m => Templates(m).Select(t => (m, Route(t))));

    private static IEnumerable<string> Templates(MethodInfo action)
    {
        var templates = action.GetCustomAttributes().OfType<IRouteTemplateProvider>().Select(a => a.Template).OfType<string>().ToArray();
        return templates.Length > 0 ? templates : new[] { string.Empty };
    }

    private static string Route(string template) =>
        template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal) ? template.TrimStart('~', '/')
        : template.Length == 0 ? Prefix
        : $"{Prefix}/{template}";

    [Fact]
    public void Every_action_says_who_may_call_it()
    {
        var unmarked = Actions()
            .Where(a => a.Action.GetCustomAttribute<AuthorizeAttribute>() is null && a.Action.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            .Select(a => a.Route);

        Assert.Empty(unmarked);
    }

    [Fact]
    public void Admin_routes_are_for_admins_and_only_they_are()
    {
        foreach (var (action, route) in Actions())
        {
            bool admin = action.GetCustomAttribute<AuthorizeAttribute>()?.Policy == "RequiresElevation";
            Assert.True(admin == route.StartsWith(Prefix + "/Admin/", StringComparison.Ordinal), $"{route} is {(admin ? string.Empty : "not ")}admin-only");
        }
    }

    [Fact]
    public void Only_the_page_itself_needs_no_sign_in()
    {
        var open = Actions().Where(a => a.Action.GetCustomAttribute<AllowAnonymousAttribute>() is not null).Select(a => a.Route);

        Assert.Equal(new[] { Prefix + "/Page" }, open);
    }
}
