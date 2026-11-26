using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.UI.Localization;

namespace LekhaBook.Application.Tests;

/// <summary>Form validation messages must exist in every language the app ships with.</summary>
public sealed class ValidationTranslationTests
{
    private static readonly ResourceManager Resources = new(typeof(SharedResource));
    private static readonly CultureInfo Nepali = CultureInfo.GetCultureInfo("ne");

    private static string Placeholders(string value) =>
        string.Concat(Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order());

    [Fact]
    public void English_resources_match_the_wording_the_server_uses()
    {
        foreach (var (key, english) in MasterValidation.Messages)
        {
            Assert.Equal(english, Resources.GetString(key, CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void Every_message_is_translated_to_Nepali_with_the_same_placeholders()
    {
        // A resource set without parents answers only from SharedResource.ne.resx, never the English fallback.
        var nepali = Resources.GetResourceSet(Nepali, createIfNotExists: true, tryParents: false);
        Assert.NotNull(nepali);

        foreach (var (key, english) in MasterValidation.Messages)
        {
            var translated = nepali.GetString(key);
            Assert.False(string.IsNullOrWhiteSpace(translated), key);
            Assert.NotEqual(english, translated);
            Assert.Equal(Placeholders(english), Placeholders(translated!));
        }
    }

    [Fact]
    public void A_form_gets_the_messages_in_its_language()
    {
        ValidationText nepali = (key, args) => string.Format(Nepali, Resources.GetString(key, Nepali)!, args);

        var errors = MasterValidation.Validate(new SavePartyRequest("", "Customer"), nepali);

        Assert.Equal("पूरा नाम आवश्यक छ।", Assert.Single(errors["name"]));
        Assert.Equal("Full name is required.", Assert.Single(MasterValidation.Validate(new SavePartyRequest("", "Customer"))["name"]));
    }
}
