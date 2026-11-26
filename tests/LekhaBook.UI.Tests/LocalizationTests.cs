using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using LekhaBook.UI.Formatting;
using LekhaBook.UI.Localization;

namespace LekhaBook.UI.Tests;

/// <summary>The screens must have every string in every language the app ships with.</summary>
public sealed class LocalizationTests
{
    private static readonly ResourceManager Resources = new(typeof(SharedResource));

    public static TheoryData<string> Translations =>
        new(Languages.Supported.Select(l => l.Code).Where(c => c != Languages.English));

    public static TheoryData<string> AllLanguages => new(Languages.Supported.Select(l => l.Code));

    private static Dictionary<string, string> Strings(CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static string Placeholders(string value) =>
        string.Concat(Regex.Matches(value, @"\{\d+").Select(m => m.Value).Distinct().Order());

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_translation_has_every_English_key_with_the_same_placeholders(string code)
    {
        var english = Strings(CultureInfo.InvariantCulture);
        var translated = Strings(CultureInfo.GetCultureInfo(code));

        Assert.NotEmpty(english);
        Assert.Empty(english.Keys.Except(translated.Keys));
        // MudBlazor already has English for its own text, so only those keys may be translation-only.
        Assert.All(translated.Keys.Except(english.Keys), key => Assert.StartsWith("Mud", key));
        foreach (var (key, value) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
            Assert.True(Placeholders(value) == Placeholders(translated[key]), key);
        }
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_language_keeps_Gregorian_dates_and_a_dot_for_decimals(string code)
    {
        var culture = Languages.CultureFor(code);

        Assert.Equal(code, culture.TwoLetterISOLanguageName);
        Assert.Equal("2026-11-26", new DateTime(2026, 11, 26).ToString("yyyy-MM-dd", culture));
        Assert.Equal("1234.50", 1234.50m.ToString("0.00", culture));
        Assert.Equal(1234.50m, decimal.Parse("1234.50", culture));
        Assert.Equal(culture.TextInfo.IsRightToLeft, Languages.IsRightToLeft(code));
    }

    [Theory]
    [InlineData("en", "-Rs. 1,23,456.00")]
    [InlineData("ar", "⁦-Rs. 1,23,456.00⁩")]
    public void Amounts_keep_their_order_in_right_to_left_text(string code, string expected)
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = Languages.CultureFor(code);
            Assert.Equal(expected, Money.Format(-123456m));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Theory]
    [InlineData("ne", "ne")]
    [InlineData("ne-NP", "ne")]
    [InlineData("bn-BD", "bn")]
    [InlineData("ar-SA", "ar")]
    [InlineData("ar-AE", "ar")]
    [InlineData("ar-OM", "ar")]
    [InlineData("zh-CN", "zh")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void A_requested_language_becomes_a_supported_one(string? requested, string expected) =>
        Assert.Equal(expected, Languages.Normalize(requested));
}
