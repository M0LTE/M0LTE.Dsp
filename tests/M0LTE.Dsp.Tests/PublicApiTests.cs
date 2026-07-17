using System.Runtime.CompilerServices;
using M0LTE.Dsp;
using PublicApiGenerator;

namespace M0LTE.Dsp.Tests;

/// <summary>
/// Locks the library's public API surface. <see cref="PublicApiGenerator"/> renders every
/// public type and member of the <c>M0LTE.Dsp</c> assembly to text; the test asserts it
/// matches the committed <c>PublicApi.approved.txt</c> snapshot. Any addition, removal or
/// signature change to the public surface fails the build until the snapshot is
/// regenerated — a deliberate act that must be paired with the correct SemVer bump
/// (see docs/versioning.md).
/// </summary>
public class PublicApiTests
{
    [Fact]
    public void Public_api_surface_matches_the_approved_snapshot()
    {
        string actual = Normalise(typeof(Fft).Assembly.GeneratePublicApi(
            new ApiGeneratorOptions { IncludeAssemblyAttributes = false }));

        string approvedPath = SnapshotPath("PublicApi.approved.txt");
        string approved = File.Exists(approvedPath)
            ? Normalise(File.ReadAllText(approvedPath))
            : string.Empty;

        if (actual != approved)
        {
            File.WriteAllText(SnapshotPath("PublicApi.received.txt"), actual);
        }

        actual.Should().Be(
            approved,
            "the public API must match PublicApi.approved.txt — if this change is intended, "
            + "copy PublicApi.received.txt over PublicApi.approved.txt and bump the package "
            + "version per docs/versioning.md");
    }

    private static string Normalise(string api) => api.ReplaceLineEndings("\n").TrimEnd() + "\n";

    private static string SnapshotPath(string fileName, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, fileName);
}
