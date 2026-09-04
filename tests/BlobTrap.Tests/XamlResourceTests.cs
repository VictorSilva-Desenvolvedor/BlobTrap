using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace BlobTrap.Tests;

/// <summary>
/// Checks that every resource key the XAML asks for actually exists.
///
/// This is worth a test because the compiler does not do it: a StaticResource pointing at a
/// missing key builds cleanly with zero warnings and then throws XamlParseException the moment
/// the window is opened. A DynamicResource fails even more quietly - the element just renders
/// unstyled. Either way the failure lands on the user, not on CI.
/// </summary>
public class XamlResourceTests
{
    private static readonly Regex StaticReference = new(@"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_.]*)\s*\}", RegexOptions.Compiled);
    private static readonly Regex DynamicReference = new(@"\{DynamicResource\s+([A-Za-z_][A-Za-z0-9_.]*)\s*\}", RegexOptions.Compiled);
    private static readonly Regex KeyDefinition = new(@"x:Key\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Keys the theme pushes in at runtime: ThemeManager.Set("Name", ...) / SetColor.</summary>
    private static readonly Regex ThemeKey = new(@"\bSet(?:Color)?\(""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Locates the repository from this source file's own path, which the compiler bakes in.
    /// Walking up from the test binary would tie the check to the default build layout and
    /// break under "dotnet test --artifacts-path" or any CI that redirects output elsewhere.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

        Assert.True(
            File.Exists(Path.Combine(root, "BlobTrap.sln")),
            $"BlobTrap.sln nao encontrado em {root}; a estrutura de pastas mudou?");

        return root;
    }

    private static string AppDirectory() => Path.Combine(RepoRoot(), "src", "BlobTrap.App");

    private static IReadOnlyList<string> XamlFiles() =>
        Directory.GetFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories);

    private static HashSet<string> DefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in XamlFiles())
        {
            foreach (Match match in KeyDefinition.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;

                // Templates keyed by type ({x:Type Button}) are not name lookups.
                if (key.StartsWith("{", StringComparison.Ordinal)) continue;
                keys.Add(key);
            }
        }

        var themeManager = Path.Combine(AppDirectory(), "Theming", "ThemeManager.cs");
        Assert.True(File.Exists(themeManager), $"ThemeManager.cs nao encontrado em {themeManager}");

        foreach (Match match in ThemeKey.Matches(File.ReadAllText(themeManager)))
            keys.Add(match.Groups[1].Value);

        return keys;
    }

    public static TheoryData<string> AllXamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in XamlFiles()) data.Add(Path.GetRelativePath(RepoRoot(), file));
        return data;
    }

    [Fact]
    public void TheProjectHasXamlToCheck()
    {
        // Guards the test itself: a broken path would otherwise make every check vacuously pass.
        Assert.NotEmpty(XamlFiles());
        Assert.NotEmpty(DefinedKeys());
    }

    [Theory]
    [MemberData(nameof(AllXamlFiles))]
    public void EveryResourceReferenceResolves(string relativePath)
    {
        var defined = DefinedKeys();
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        var missing = new List<string>();

        foreach (Match match in StaticReference.Matches(text))
        {
            var key = match.Groups[1].Value;
            if (!defined.Contains(key)) missing.Add($"StaticResource {key}");
        }

        foreach (Match match in DynamicReference.Matches(text))
        {
            var key = match.Groups[1].Value;
            if (!defined.Contains(key)) missing.Add($"DynamicResource {key}");
        }

        Assert.True(
            missing.Count == 0,
            $"{relativePath} referencia chaves inexistentes: {string.Join(", ", missing.Distinct())}");
    }

    [Fact]
    public void ThemeDefinesTheSameKeysInBothThemes()
    {
        // A key set in only one branch leaves that theme with an unresolved brush, which is
        // invisible until someone switches Windows to light mode.
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "Theming", "ThemeManager.cs"));

        var darkStart = source.IndexOf("if (theme == AppTheme.Dark)", StringComparison.Ordinal);
        var lightStart = source.IndexOf("else", darkStart, StringComparison.Ordinal);
        var lightEnd = source.IndexOf("var fill = accent.FillFor(theme);", lightStart, StringComparison.Ordinal);

        Assert.True(darkStart >= 0 && lightStart > darkStart && lightEnd > lightStart,
            "Nao consegui localizar os blocos de tema em ThemeManager.cs.");

        var dark = ThemeKey.Matches(source[darkStart..lightStart]).Select(m => m.Groups[1].Value).ToHashSet();
        var light = ThemeKey.Matches(source[lightStart..lightEnd]).Select(m => m.Groups[1].Value).ToHashSet();

        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));
    }
}
