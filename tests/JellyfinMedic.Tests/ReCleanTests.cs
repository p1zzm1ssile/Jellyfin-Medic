using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

/// <summary>Cleaning a file whose original is already kept, with real files in a temp folder.</summary>
public sealed class ReCleanTests : IDisposable
{
    private readonly string _dir;
    private readonly string _film;
    private readonly string _kept;

    public ReCleanTests()
    {
        _dir = Path.Combine(Directory.CreateTempSubdirectory("medic-reclean-").FullName, "Film (2020)");
        Directory.CreateDirectory(_dir);
        _film = Path.Combine(_dir, "Film (2020).mkv");
        _kept = Path.Combine(_dir, ".medic-originals", "Film (2020).mkv");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);

    private string Folder => Path.GetDirectoryName(_kept)!;

    private string Temp(string text)
    {
        string temp = _film + ".medic-tmp.mkv";
        File.WriteAllText(temp, text);
        return temp;
    }

    private Task<(long FreedNow, long KeptNow, string? KeptAs)> Clean(string text) =>
        TrackCleaner.SwapKeepingOriginal(_film, Temp(text), "Film (2020)", new FileInfo(_film).Length, 1, CancellationToken.None);

    /// <summary>A title cleaned by 1.0.22 or earlier: the original kept, the cleaned file in place, and no record.</summary>
    private async Task CleanedWithoutRecord()
    {
        await TrackCleaner.EnsureOriginalsFolder(_film, CancellationToken.None);
        File.WriteAllText(_kept, "true original");
        File.WriteAllText(_film, "cleaned once");
    }

    private string[] Aside() => Directory.GetFiles(Folder, "*.replaced-*");

    private static TrackCleaner.ProbedFile Probed() =>
        new(new List<(int, string, string)> { (0, "video", string.Empty), (1, "audio", "eng") }, 100);

    [Fact]
    public async Task Re_cleaning_a_file_with_no_record_keeps_its_true_original()
    {
        await CleanedWithoutRecord();

        var result = await Clean("cleaned twice");

        Assert.Equal("true original", File.ReadAllText(_kept));
        Assert.Equal("cleaned twice", File.ReadAllText(_film));
        Assert.Empty(Aside());
        Assert.False(TrackCleaner.IsOrphan(_dir, _kept));
        Assert.Equal(_kept, result.KeptAs);
        Assert.Equal((1L, 0L), (result.FreedNow, result.KeptNow));
    }

    [Fact]
    public async Task Re_cleaning_a_file_medic_recorded_says_its_original_is_kept()
    {
        File.WriteAllText(_film, "true original");
        var first = await Clean("cleaned once");
        Assert.Equal(_kept, first.KeptAs);

        var again = await Clean("cleaned twice");

        Assert.Equal(_kept, again.KeptAs);
        Assert.Equal("true original", File.ReadAllText(_kept));
        Assert.Empty(Aside());
    }

    [Fact]
    public async Task A_recorded_file_replaced_since_keeps_both_originals()
    {
        File.WriteAllText(_film, "true original");
        await Clean("cleaned once");
        File.WriteAllText(_film, "a new release, longer");

        var result = await Clean("new release cleaned");

        Assert.Equal("a new release, longer", File.ReadAllText(_kept));
        Assert.Equal("true original", File.ReadAllText(Assert.Single(Aside())));
        Assert.Equal(_kept, result.KeptAs);
    }

    [Fact]
    public async Task A_recorded_file_whose_time_alone_changed_keeps_its_true_original()
    {
        File.WriteAllText(_film, "true original");
        await Clean("cleaned once");
        File.SetLastWriteTimeUtc(_film, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var result = await Clean("cleaned twice");

        Assert.Equal("true original", File.ReadAllText(_kept));
        Assert.Empty(Aside());
        Assert.Equal(_kept, result.KeptAs);
    }

    [Fact]
    public async Task A_record_without_a_length_keeps_the_true_original()
    {
        await CleanedWithoutRecord();
        File.WriteAllText(Path.Combine(Folder, ".medic-cleaned.json"), "{\"Film (2020).mkv\":{\"WrittenUtc\":\"2020-01-02T03:04:05Z\"}}");

        await Clean("cleaned twice");

        Assert.Equal("true original", File.ReadAllText(_kept));
        Assert.Empty(Aside());
    }

    [Fact]
    public async Task A_copy_tidy_put_in_place_is_recorded_so_a_later_release_is_told_apart()
    {
        File.WriteAllText(_film, "true original");
        File.WriteAllText(Path.ChangeExtension(_film, ".medic-stripped.mkv"), "stripped by 1.0.5");

        await TrackCleaner.TidyFolder(_dir, _ => Task.FromResult<TrackCleaner.ProbedFile?>(Probed()), false, CancellationToken.None);

        Assert.Equal("stripped by 1.0.5", File.ReadAllText(_film));
        Assert.Contains("Film (2020).mkv", File.ReadAllText(Path.Combine(Folder, ".medic-cleaned.json")), StringComparison.Ordinal);

        File.WriteAllText(_film, "a new release, longer");
        await Clean("new release cleaned");

        Assert.Equal("a new release, longer", File.ReadAllText(_kept));
        Assert.Equal("true original", File.ReadAllText(Assert.Single(Aside())));
    }

    [Fact]
    public async Task Deleting_the_last_kept_original_removes_the_folder_with_medics_own_files()
    {
        File.WriteAllText(_film, "true original");
        await Clean("cleaned once");
        File.WriteAllText(Path.Combine(Folder, ".medic-cleaned.json.tmp"), "{");
        File.WriteAllText(Path.ChangeExtension(_film, ".medic-stripped.mkv"), "stripped by 1.0.5");

        await TrackCleaner.TidyFolder(_dir, _ => Task.FromResult<TrackCleaner.ProbedFile?>(Probed()), true, CancellationToken.None);

        Assert.Equal("stripped by 1.0.5", File.ReadAllText(_film));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task A_folder_still_holding_another_file_is_kept()
    {
        File.WriteAllText(_film, "true original");
        await Clean("cleaned once");
        File.WriteAllText(Path.Combine(Folder, "Other (2021).mkv"), "another original");
        File.WriteAllText(Path.ChangeExtension(_film, ".medic-stripped.mkv"), "stripped by 1.0.5");

        await TrackCleaner.TidyFolder(_dir, _ => Task.FromResult<TrackCleaner.ProbedFile?>(Probed()), true, CancellationToken.None);

        Assert.False(File.Exists(_kept));
        Assert.Equal("another original", File.ReadAllText(Path.Combine(Folder, "Other (2021).mkv")));
    }
}
