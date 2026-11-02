using System.Reflection;
using JellyfinMedic.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMedic.Tests;

/// <summary>Every route of Jellyfin Medic is for admins, apart from the page's own scripts and styles.</summary>
public class AdminAccessTests
{
    private static readonly Type[] Controllers = typeof(MedicController).Assembly.GetTypes()
        .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
        .ToArray();

    [Fact]
    public void Every_controller_but_the_assets_is_admin_only()
    {
        var open = Controllers
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>()?.Policy != "RequiresElevation")
            .Select(t => t.Name);

        Assert.Equal(new[] { "MedicAssetsController" }, open);
    }

    [Fact]
    public void No_action_opens_itself_to_everyone()
    {
        var open = Controllers
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name);

        Assert.Empty(open);
    }
}
