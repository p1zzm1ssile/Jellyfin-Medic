using System.Xml.Linq;
using JellyfinMedic.Api;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class PluginRulesTests
{
    [Fact]
    public void SMTP_MQTT_and_Pushover_destinations_without_an_address_are_not_called_empty()
    {
        var findings = Webhook(Destination("SmtpOptions", "Mail"), Destination("MqttOptions", "Home Assistant"), Destination("PushoverOptions", "Phone"));

        Assert.DoesNotContain(findings, f => f.Title.Contains("empty destination", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Discord_destination_without_an_address_is_still_called_empty()
    {
        var findings = Webhook(Destination("DiscordOptions", "Discord"), Destination("SmtpOptions", "Mail"));

        Assert.Equal("Webhook has an empty destination", Assert.Single(findings).Title);
    }

    [Fact]
    public void A_destination_type_Medic_doesnt_know_is_not_called_empty()
    {
        Assert.Empty(Webhook(Destination("TeamsOptions", "Teams")));
    }

    [Fact]
    public void A_placeholder_in_an_SMTP_or_MQTT_address_is_not_extra_text()
    {
        // Webhook's settings page asks every type for an address, so SMTP and MQTT destinations can hold a placeholder.
        var findings = Webhook(Destination("SmtpOptions", "Mail", "not used"), Destination("MqttOptions", "Home Assistant", "not used"));

        Assert.DoesNotContain(findings, f => f.Title.Contains("extra text", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_text_after_a_Pushover_address_is_still_a_problem()
    {
        // Pushover sends to the address when one is set, and only falls back to its own when it's empty.
        var findings = Webhook(Destination("PushoverOptions", "Phone", "https://api.pushover.net/1/messages.json`"));

        Assert.Equal("Webhook's \"Phone\" address has extra text on the end", Assert.Single(findings).Title);
    }

    private static List<Finding> Webhook(params XElement[] destinations)
    {
        var report = new PluginReport { Name = "Webhook" };
        PluginRules.Apply(report, "Jellyfin.Plugin.Webhook.xml", new XElement("PluginConfiguration", destinations), new Dictionary<string, XElement>(), new PluginContext());
        return report.Findings;
    }

    // Webhook saves each type's list as, for example, <SmtpOptions><SmtpOption>...</SmtpOption></SmtpOptions>.
    private static XElement Destination(string type, string name, string? uri = null) =>
        new(type, new XElement(type[..^1],
            new XElement("WebhookName", name),
            uri is null ? null : new XElement("WebhookUri", uri),
            new XElement("EnableWebhook", true)));
}
