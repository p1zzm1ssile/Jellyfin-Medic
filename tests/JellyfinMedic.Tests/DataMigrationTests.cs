namespace JellyfinMedic.Tests;

public sealed class DataMigrationTests : IDisposable
{
    private readonly FakeApplicationPaths _paths = new();

    private string Medic => Path.Combine(_paths.PluginConfigurationsPath, "JellyfinMedic");

    private string TaskAdvisor => Path.Combine(_paths.PluginConfigurationsPath, "TaskAdvisor");

    [Fact]
    public void A_file_deleted_after_the_first_start_stays_deleted()
    {
        Directory.CreateDirectory(TaskAdvisor);
        File.WriteAllText(Path.Combine(TaskAdvisor, "ignored_findings.json"), "[\"a finding\"]");

        DataMigration.Run(_paths);
        string copy = Path.Combine(Medic, "ignored_findings.json");
        Assert.True(File.Exists(copy));

        File.Delete(copy); // the admin resets ignored findings
        DataMigration.Run(_paths);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Medic_data_from_before_the_marker_is_not_topped_up_from_old_folders()
    {
        // Medic has run before (it has its own data), and a Task Advisor file is missing from its folder.
        Directory.CreateDirectory(Medic);
        File.WriteAllText(Path.Combine(Medic, "run_history.json"), "[]");
        Directory.CreateDirectory(TaskAdvisor);
        File.WriteAllText(Path.Combine(TaskAdvisor, "ignored_findings.json"), "[\"a finding\"]");

        DataMigration.Run(_paths);

        Assert.False(File.Exists(Path.Combine(Medic, "ignored_findings.json")));
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);
}
