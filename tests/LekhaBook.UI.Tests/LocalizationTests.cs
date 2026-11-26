using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using LekhaBook.UI.Localization;

namespace LekhaBook.UI.Tests;

/// <summary>The screens must have every string in every language the app ships with.</summary>
public sealed class LocalizationTests
{
    private static readonly ResourceManager Resources = new(typeof(SharedResource));

    private static Dictionary<string, string> Strings(CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static string Placeholders(string value) =>
        string.Concat(Regex.Matches(value, @"\{\d+").Select(m => m.Value).Distinct().Order());

    [Fact]
    public void Nepali_has_the_same_keys_and_placeholders_as_English()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        var nepali = Strings(CultureInfo.GetCultureInfo(Languages.Nepali));

        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(nepali.Keys));
        // MudBlazor already has English for its own text, so only those keys may be Nepali-only.
        Assert.All(nepali.Keys.Except(english.Keys), key => Assert.StartsWith("Mud", key));
        foreach (var (key, value) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(nepali[key]), key);
            Assert.True(Placeholders(value) == Placeholders(nepali[key]), key);
        }
    }

    [Theory]
    [InlineData("ne", "ne")]
    [InlineData("ne-NP", "ne")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void A_requested_language_becomes_a_supported_one(string? requested, string expected) =>
        Assert.Equal(expected, Languages.Normalize(requested));
}
