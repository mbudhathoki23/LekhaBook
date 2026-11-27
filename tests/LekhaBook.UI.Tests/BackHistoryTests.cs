using LekhaBook.UI.Services;

namespace LekhaBook.UI.Tests;

public sealed class BackHistoryTests
{
    private static BackHistory After(params string[] screens)
    {
        var history = new BackHistory();
        foreach (var screen in screens)
        {
            history.Visit(screen);
        }

        return history;
    }

    [Theory]
    [InlineData("")]
    [InlineData("signin")]
    [InlineData("signin?returnUrl=parties")]
    public void The_first_screen_leaves_the_app_whatever_came_before(string screen) =>
        Assert.Null(After("parties", screen).Back());

    [Fact]
    public void Each_press_goes_one_screen_further_back_until_the_first()
    {
        var history = After("", "parties", "parties/7");

        Assert.Equal("parties", history.Back());
        history.Visit("parties"); // the navigation that follows
        Assert.Equal("", history.Back());
        history.Visit("");
        Assert.Null(history.Back());
    }

    [Theory]
    [InlineData("transactions/new")]
    [InlineData("transactions/new?type=PaymentIn")]
    [InlineData("parties/7/edit")]
    public void A_finished_form_is_not_returned_to(string form) =>
        Assert.Equal("", After("", form, "transactions").Back());

    [Fact]
    public void A_form_goes_back_to_the_screen_it_was_opened_from() =>
        Assert.Equal("parties?filter=due", After("", "parties?filter=due", "parties/new").Back());

    [Fact]
    public void Going_back_through_a_link_does_not_make_Back_go_forward_again()
    {
        var history = After("", "parties", "parties/7", "parties");

        Assert.Equal("", history.Back());
    }

    [Fact]
    public void A_signed_in_screen_never_goes_back_to_sign_in() =>
        Assert.Equal("", After("", "signin", "", "parties").Back());

    [Fact]
    public void Sign_up_goes_back_to_sign_in() =>
        Assert.Equal("signin", After("signin", "signup").Back());

    [Fact]
    public void A_screen_opened_with_nothing_before_it_goes_to_the_first_screen() =>
        Assert.Equal("", After("parties/7").Back());
}
