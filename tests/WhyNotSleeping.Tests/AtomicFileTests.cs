namespace WhyNotSleeping.Tests;

public class AtomicFileTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "WhyNotSleeping.Tests", Guid.NewGuid().ToString("N"));
    string PathFor(string name) => Path.Combine(_folder, name);

    [Fact]
    public void Settings_create_a_file_then_replace_it_with_valid_json()
    {
        var path = PathFor("settings.json");
        var settings = new Settings { AwayMinutes = 15 };
        settings.Save(path);
        Assert.Equal(15, Settings.Parse(File.ReadAllText(path)).AwayMinutes);
        settings.AwayMinutes = 20;
        settings.Save(path);
        Assert.Equal(20, Settings.Parse(File.ReadAllText(path)).AwayMinutes);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void An_interrupted_write_keeps_the_original_and_cleans_up_the_temporary_file()
    {
        var path = PathFor("settings.json");
        new Settings { AwayMinutes = 15 }.Save(path);
        var original = File.ReadAllText(path);
        Assert.Throws<IOException>(() => AtomicFile.Write(path, writer =>
        {
            writer.Write("partly written");
            throw new IOException("Simulated interrupted write.");
        }));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_failed_replace_keeps_the_original_settings()
    {
        var path = PathFor("settings.json");
        new Settings { AwayMinutes = 15 }.Save(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            new Settings { AwayMinutes = 20 }.Save(path);
        Assert.Equal(15, Settings.Parse(File.ReadAllText(path)).AwayMinutes);
        Assert.Single(Directory.GetFiles(_folder));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
}
