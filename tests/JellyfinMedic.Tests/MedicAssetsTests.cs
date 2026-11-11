using JellyfinMedic.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMedic.Tests;

/// <summary>The files the Medic page loads by address: every tab script is served, and nothing else is.</summary>
public class MedicAssetsTests
{
    private static readonly string[] Resources = typeof(MedicAssetsController).Assembly.GetManifestResourceNames();

    [Fact]
    public void Every_tab_script_is_served()
    {
        var tabs = Resources.Where(r => r.StartsWith("JellyfinMedic.medic-", StringComparison.Ordinal) && r.EndsWith(".js", StringComparison.Ordinal));
        var served = MedicAssetsController.Files.Values.Select(f => f.Resource);

        Assert.NotEmpty(tabs);
        Assert.Empty(tabs.Except(served));
    }

    [Fact]
    public void Every_served_file_is_in_the_plugin()
    {
        Assert.Empty(MedicAssetsController.Files.Values.Select(f => f.Resource).Except(Resources));
    }

    [Theory]
    [InlineData("medic.css", "text/css; charset=utf-8", "#JellyfinMedicPage")]
    [InlineData("medic-iptv.js", "text/javascript; charset=utf-8", "JellyfinMedicTabs")]
    [InlineData("medic-media.js", "text/javascript; charset=utf-8", "JellyfinMedicTabs")]
    public void Serves_a_listed_file_with_its_type(string name, string contentType, string inside)
    {
        var controller = new MedicAssetsController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var file = Assert.IsType<FileStreamResult>(controller.GetAsset(name));

        Assert.Equal(contentType, file.ContentType);
        Assert.Contains(inside, new StreamReader(file.FileStream).ReadToEnd(), StringComparison.Ordinal);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData("medic.html")]
    [InlineData("assist.js")]
    [InlineData("iptv.js")]
    [InlineData("media.js")]
    [InlineData("../medic.html")]
    public void Serves_nothing_else(string name)
    {
        var controller = new MedicAssetsController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<NotFoundResult>(controller.GetAsset(name));
    }
}
