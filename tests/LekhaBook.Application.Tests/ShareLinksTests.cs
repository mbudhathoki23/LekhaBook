namespace LekhaBook.Application.Tests;

public sealed class ShareLinksTests
{
    [Theory]
    [InlineData("9801234567", "9779801234567")]
    [InlineData("980-123 4567", "9779801234567")]
    [InlineData("+977 980 123 4567", "9779801234567")]
    [InlineData("009779801234567", "9779801234567")]
    [InlineData("9779801234567", "9779801234567")]
    [InlineData("+91 98765 43210", "919876543210")]
    [InlineData("01-4412345", null)] // a landline: let the user pick the contact
    [InlineData("12345", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_phone_becomes_a_WhatsApp_number_only_when_it_is_certain(string? phone, string? expected) =>
        Assert.Equal(expected, ShareLinks.WhatsAppNumber(phone));

    [Fact]
    public void WhatsApp_keeps_the_whole_message_including_links_and_Nepali_text()
    {
        const string message = "नमस्ते\nhttps://lekhabooknepal.com/r/a_b-c?x=1&y=2";

        var url = ShareLinks.WhatsApp(message, "9801234567");

        Assert.StartsWith("https://wa.me/9779801234567?text=", url);
        Assert.Equal(message, Uri.UnescapeDataString(url.Split("?text=")[1]));
        Assert.DoesNotContain('&', url);
    }

    [Fact]
    public void Without_a_phone_WhatsApp_asks_whom_to_forward_to() =>
        Assert.Equal("https://wa.me/?text=hi", ShareLinks.WhatsApp("hi"));

    [Fact]
    public void An_email_draft_has_the_address_subject_and_body()
    {
        var url = ShareLinks.Email("A & B", "line 1\nline 2", "ram@example.com");

        Assert.Equal("mailto:ram@example.com?subject=A%20%26%20B&body=line%201%0D%0Aline%202", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an address")]
    [InlineData("a@example.com?bcc=other@example.com")]
    public void A_missing_or_odd_address_is_left_for_the_user_to_fill_in(string? address) =>
        Assert.StartsWith("mailto:?subject=", ShareLinks.Email("s", "b", address));
}
