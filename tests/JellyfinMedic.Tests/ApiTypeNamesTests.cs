using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinMedic.Tests;

public sealed class ApiTypeNamesTests
{
    // Jellyfin names the schemas in its API document by a type's short name, so a type Medic's routes return or take
    // must not share one with a Jellyfin type, or with another of Medic's: /api-docs/openapi.json then fails to build.
    [Fact]
    public void No_type_in_Medics_API_shares_its_name_with_a_Jellyfin_type_or_another_Medic_type()
    {
        var jellyfin = new[]
            {
                typeof(MediaBrowser.Model.Session.TranscodeReason),
                typeof(MediaBrowser.Controller.Library.IUserManager),
                typeof(MediaBrowser.Common.Plugins.IPlugin),
                typeof(Jellyfin.Data.Events.GenericEventArgs<>)
            }
            .Select(t => t.Assembly)
            .Distinct()
            .SelectMany(a => a.GetExportedTypes())
            .Select(ShortName)
            .ToHashSet(StringComparer.Ordinal);
        var medic = ApiTypes();

        var clashes = medic.Where(t => jellyfin.Contains(ShortName(t)))
            .Concat(medic.GroupBy(ShortName).Where(g => g.Count() > 1).SelectMany(g => g))
            .Select(t => t.FullName)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(clashes.Count == 0, "Rename these: " + string.Join(", ", clashes));
    }

    private static string ShortName(Type type) => type.Name.Split('`')[0];

    // Every Medic type a route returns or takes, and every Medic type their properties use, however deep.
    private static List<Type> ApiTypes()
    {
        var medic = typeof(JellyfinMedic.Api.MedicController).Assembly;
        var pending = new Queue<Type>(medic.GetExportedTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
        var found = new HashSet<Type>();
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (type.IsArray)
            {
                pending.Enqueue(type.GetElementType()!);
                continue;
            }

            foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes)
            {
                pending.Enqueue(argument);
            }

            // A generic type counts once, by its definition: PagedList<FilePlan> and PagedList<UntaggedTrack> get schemas
            // named after their type arguments, so they don't clash with each other.
            if (type.Assembly == medic && found.Add(type.IsGenericType ? type.GetGenericTypeDefinition() : type))
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    pending.Enqueue(property.PropertyType);
                }
            }
        }

        return found.ToList();
    }
}
