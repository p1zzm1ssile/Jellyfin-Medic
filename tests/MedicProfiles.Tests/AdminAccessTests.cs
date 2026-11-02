using System.Reflection;
using Jellyfin.Plugin.MedicProfiles.Api;
using Microsoft.AspNetCore.Authorization;

namespace MedicProfiles.Tests;

/// <summary>Every route is for admins: the class says so, and no action opens itself up.</summary>
public class AdminAccessTests
{
    [Fact]
    public void Every_route_is_admin_only()
    {
        Assert.Equal("RequiresElevation", typeof(ProfilesController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.DoesNotContain(typeof(ProfilesController).GetMethods(), m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }
}
