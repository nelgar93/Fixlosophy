using System.Reflection;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Bunit;
using Fixlosophy.Components.Pages;
using Fixlosophy.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Fixlosophy.Tests;

/// <summary>
/// The booking wizard's glue: step gating, the order its validators run in, which
/// branch each guard takes, and what the confirm handler passes on.
/// </summary>
/// <remarks>
/// What is deliberately absent: the rules themselves. Whether "a@" is a valid address
/// belongs to <see cref="AuthServiceTests"/>, whether a slot is free to
/// <see cref="AvailabilityTests"/>, whether a booking saves to
/// <see cref="BookingServiceTests"/>. Asserting them again here would mean two places
/// to edit per rule change and no extra safety. What is left is the wiring — which is
/// what no service test can see, and where this page's own bugs would live.
/// </remarks>
public class BookWizardTests : ComponentTestBase
{
    // ---------------------------------------------------------------- driving

    private IRenderedComponent<Book> RenderWizard(Task<AuthenticationState>? signedIn = null) =>
        signedIn is null
            ? Render<Book>()
            : Render<Book>(ps => ps.AddCascadingValue(signedIn));

    /// The step's forward button — every step keeps it in the same place, so the
    /// helpers below don't need to know which step they are on.
    private static IElement Forward(IRenderedComponent<Book> cut) =>
        cut.Find(".booking-nav .btn-primary");

    private static void ChooseService(IRenderedComponent<Book> cut)
    {
        cut.FindAll(".service-pick-card")[0].Click();
        Forward(cut).Click();
    }

    /// <summary>Picks the first bookable day and slot, then moves to step 3.</summary>
    /// <remarks>
    /// Pages to next month first, deliberately. The calendar opens on the current one,
    /// where how many days are still bookable depends on today's date — run this suite
    /// on the 30th and there may be none left. A full month ahead is always bookable
    /// and always inside the booking horizon, so the flow is the same in January as on
    /// a month's last afternoon.
    /// </remarks>
    private static void ChooseSlot(IRenderedComponent<Book> cut)
    {
        cut.Find("button[aria-label='Next month']").Click();
        cut.FindAll(".cal-day--available")[0].Click();
        cut.FindAll(".timeslot")[0].Click();
        Forward(cut).Click();
    }

    private static void FillDetails(
        IRenderedComponent<Book> cut,
        string name = "Jane Smith",
        string email = "jane@example.com",
        string phone = "07700 900000")
    {
        cut.Find("#booking-name").Change(name);
        // Signed in, the address is fixed to the account's and the field carries no
        // handler at all — see SignedInCustomer_CannotEditTheEmailField.
        var emailField = cut.Find("#booking-email");
        if (!emailField.HasAttribute("readonly")) emailField.Change(email);
        cut.Find("#booking-phone").Change(phone);
    }

    /// Service, slot, details — leaving the wizard on the review step.
    private static void DriveToReview(IRenderedComponent<Book> cut, string email = "jane@example.com")
    {
        ChooseService(cut);
        ChooseSlot(cut);
        FillDetails(cut, email: email);
        Forward(cut).Click();
    }

    private static string? Error(IRenderedComponent<Book> cut) =>
        cut.FindAll(".booking-error").Count == 0
            ? null
            : cut.Find(".booking-error").TextContent.Trim();

    private static bool OnReviewStep(IRenderedComponent<Book> cut) =>
        cut.FindAll(".review-grid").Count == 1;

    /// An upcoming booking for the given address, written straight to the database:
    /// what the cap counts is rows, and going through the service would need a free
    /// slot per row for no gain.
    private void AddUpcomingBooking(string email) =>
        Db.Bookings.Add(new Booking
        {
            Reference     = $"FIX-TEST-{Db.Bookings.Count() + 1:D3}",
            CustomerName  = "Jane Smith",
            CustomerEmail = email,
            CustomerPhone = "07700 900000",
            ServiceName   = "Basic Service",
            SlotDate      = TestFactory.FutureWorkday(14),
            SlotTime      = "09:00",
            Status        = BookingStatus.Confirmed
        });

    // ------------------------------------------------------------ step gating

    [Fact]
    public void Step1_CannotContinue_UntilAServiceIsChosen()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();

        Assert.True(Forward(cut).HasAttribute("disabled"));

        cut.FindAll(".service-pick-card")[0].Click();
        Assert.False(Forward(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Step2_CannotContinue_UntilBothADateAndATimeAreChosen()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);

        Assert.True(Forward(cut).HasAttribute("disabled"));

        cut.Find("button[aria-label='Next month']").Click();
        cut.FindAll(".cal-day--available")[0].Click();
        // A date on its own is not enough — the slot is the half that can go stale.
        Assert.True(Forward(cut).HasAttribute("disabled"));

        cut.FindAll(".timeslot")[0].Click();
        Assert.False(Forward(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Calendar_WillNotPageBackPastTheCurrentMonth()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);

        Assert.True(cut.Find("button[aria-label='Previous month']").HasAttribute("disabled"));

        cut.Find("button[aria-label='Next month']").Click();
        Assert.False(cut.Find("button[aria-label='Previous month']").HasAttribute("disabled"));

        // Back to where it started, and bounded again.
        cut.Find("button[aria-label='Previous month']").Click();
        Assert.True(cut.Find("button[aria-label='Previous month']").HasAttribute("disabled"));
    }

    [Fact]
    public void ChangingTheDay_ClearsTheSlotChosenUnderTheOldOne()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        cut.Find("button[aria-label='Next month']").Click();
        cut.FindAll(".cal-day--available")[0].Click();
        cut.FindAll(".timeslot")[0].Click();

        cut.FindAll(".cal-day--available")[1].Click();

        // Otherwise a slot picked under Tuesday is carried onto Wednesday, where it may
        // not be free — with the forward button still enabled.
        Assert.Empty(cut.FindAll(".timeslot--selected"));
        Assert.True(Forward(cut).HasAttribute("disabled"));
    }

    // ------------------------------------------------- step 3 validation order

    [Fact]
    public void Step3_AsksForAMissingName_First()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);

        // Every field is wrong. The name is the one it should mention.
        FillDetails(cut, name: "", email: "not-an-address", phone: "call me");
        Forward(cut).Click();

        Assert.Contains("name", Error(cut), StringComparison.OrdinalIgnoreCase);
        Assert.False(OnReviewStep(cut));
    }

    [Fact]
    public void Step3_RejectsALoneAtSign_AsAnEmailAddress()
    {
        // Regression: this check was once a bare Contains('@'), which "@" passed.
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);

        FillDetails(cut, email: "@");
        Forward(cut).Click();

        Assert.Contains("email", Error(cut), StringComparison.OrdinalIgnoreCase);
        Assert.False(OnReviewStep(cut));
    }

    [Fact]
    public void Step3_RejectsAPhoneNumberNobodyCanRing()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);

        FillDetails(cut, phone: "call me");
        Forward(cut).Click();

        Assert.Contains("phone", Error(cut), StringComparison.OrdinalIgnoreCase);
        Assert.False(OnReviewStep(cut));
    }

    [Fact]
    public void Step3_WithEverythingFilledIn_ReachesTheReviewStep()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);
        FillDetails(cut);

        Forward(cut).Click();

        Assert.True(OnReviewStep(cut));
        Assert.Null(Error(cut));
    }

    // ------------------------------------------------------------ booking cap

    [Fact]
    public void Guest_AtTheirCap_IsToldOnStep3_AndGoesNoFurther()
    {
        TestFactory.AddService(Db);
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            AddUpcomingBooking("jane@example.com");
        Db.SaveChanges();

        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);
        FillDetails(cut, email: "jane@example.com");
        Forward(cut).Click();

        // Held on step 3 rather than advanced, so a mistyped address can be corrected
        // without starting over.
        Assert.False(OnReviewStep(cut));
        Assert.Contains("upcoming bookings", Error(cut), StringComparison.Ordinal);
    }

    [Fact]
    public void Guest_UnderTheirCap_IsUnaffected()
    {
        TestFactory.AddService(Db);
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail - 1; i++)
            AddUpcomingBooking("jane@example.com");
        Db.SaveChanges();

        var cut = RenderWizard();
        DriveToReview(cut);

        Assert.True(OnReviewStep(cut));
    }

    [Fact]
    public void CancelledBookings_DoNotCountTowardsTheCap()
    {
        TestFactory.AddService(Db);
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            AddUpcomingBooking("jane@example.com");
        foreach (var booking in Db.Bookings.Local) booking.Status = BookingStatus.Cancelled;
        Db.SaveChanges();

        var cut = RenderWizard();
        DriveToReview(cut);

        Assert.True(OnReviewStep(cut));
    }

    [Fact]
    public void SignedInCustomer_AtTheirCap_IsStoppedOnStep1()
    {
        // A signed-in customer's address is known before they touch anything, so the
        // cap is answered up front rather than four steps in.
        TestFactory.AddService(Db);
        var customer = TestFactory.AddCustomer(Db);
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            AddUpcomingBooking(customer.Email);
        Db.SaveChanges();

        var cut = RenderWizard(SignedInAs(customer));
        cut.FindAll(".service-pick-card")[0].Click();

        Assert.True(Forward(cut).HasAttribute("disabled"));
    }

    // --------------------------------------------------- the account's address

    [Fact]
    public void SignedInCustomer_CannotEditTheEmailField()
    {
        TestFactory.AddService(Db);
        var customer = TestFactory.AddCustomer(Db, email: "real@example.com");

        var cut = RenderWizard(SignedInAs(customer));
        ChooseService(cut);
        ChooseSlot(cut);

        var field = cut.Find("#booking-email");
        Assert.True(field.HasAttribute("readonly"));
        // Not merely styled read-only: the field carries no change handler at all, so a
        // client that sends one anyway has nothing to bind to.
        Assert.Throws<MissingEventHandlerException>(() => field.Change("attacker@example.com"));
    }

    [Fact]
    public void SignedInCustomer_BookingIsFiledAgainstTheAccountAddress()
    {
        // The confirmation email goes to whatever address is stored here. Taking it
        // from the form would let anyone with an account send Fixlosophy-branded mail
        // to an arbitrary recipient, so ConfirmBooking prefers the account's own.
        TestFactory.AddService(Db);
        var customer = TestFactory.AddCustomer(Db, email: "real@example.com");

        var cut = RenderWizard(SignedInAs(customer));
        ChooseService(cut);
        ChooseSlot(cut);
        FillDetails(cut);
        Forward(cut).Click();
        Forward(cut).Click();

        var booking = Assert.Single(Db.Bookings);
        Assert.Equal("real@example.com", booking.CustomerEmail);
        Assert.Equal(customer.Id, booking.CustomerId);
        Assert.Equal("real@example.com", Assert.Single(Email.Confirmations).Booking.CustomerEmail);
    }

    [Fact]
    public void SignedInCustomer_ATamperedAddress_IsIgnoredInFavourOfTheAccounts()
    {
        // White-box, deliberately. Through the UI the field is unreachable — that is
        // the first line of defence and the test above covers it — so the only way to
        // reach the second one is to put the page into the state a tampered circuit
        // message would put it in, and check what gets filed.
        //
        // This is the test that fails if someone makes the field editable again and
        // forgets the guard, which is the whole reason the guard is written the way it
        // is. If the field is renamed, fix the name here; deleting the test throws away
        // the only coverage of a documented attack.
        TestFactory.AddService(Db);
        var customer = TestFactory.AddCustomer(Db, email: "real@example.com");

        var cut = RenderWizard(SignedInAs(customer));
        ChooseService(cut);
        ChooseSlot(cut);
        FillDetails(cut);
        Forward(cut).Click();

        var field = typeof(Book).GetField("customerEmail", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(cut.Instance, "attacker@example.com");

        Forward(cut).Click();

        var booking = Assert.Single(Db.Bookings);
        Assert.Equal("real@example.com", booking.CustomerEmail);
        Assert.Equal("real@example.com", Assert.Single(Email.Confirmations).Booking.CustomerEmail);
    }

    [Fact]
    public void SignedInCustomer_HasTheirDetailsPrefilled()
    {
        TestFactory.AddService(Db);
        var customer = TestFactory.AddCustomer(Db, fullName: "Ada Rider", phone: "07700 900321");

        var cut = RenderWizard(SignedInAs(customer));
        ChooseService(cut);
        ChooseSlot(cut);

        Assert.Equal("Ada Rider", ((IHtmlInputElement)cut.Find("#booking-name")).Value);
        Assert.Equal("07700 900321", ((IHtmlInputElement)cut.Find("#booking-phone")).Value);
    }

    // ------------------------------------------------------------- confirming

    [Fact]
    public void Confirm_CreatesTheBooking_AndSendsBothEmails()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        DriveToReview(cut);

        Forward(cut).Click();

        var booking = Assert.Single(Db.Bookings);
        Assert.Equal("jane@example.com", booking.CustomerEmail);
        Assert.Equal("Jane Smith", booking.CustomerName);
        // One to the customer, one to the shop — sent separately, so a bounce at the
        // customer's address still tells the shop a bike is coming.
        Assert.Single(Email.Confirmations);
        Assert.Single(Email.ShopNotifications);
        Assert.Single(Db.Notifications);
    }

    [Fact]
    public void Confirm_WhenEveryEmailFails_StillConfirmsTheBooking()
    {
        // Mail is best-effort at this call site: the booking is already committed and
        // the customer is about to be told it is confirmed.
        var throwing = new RecordingEmailSender { ThrowOnSend = true };
        Services.AddSingleton<IEmailSender>(throwing);

        TestFactory.AddService(Db);
        var cut = RenderWizard();
        DriveToReview(cut);

        Forward(cut).Click();

        Assert.Single(Db.Bookings);
        Assert.Contains("booked in", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Confirm_PastTheRateLimit_IsRefused_AndLetsYouTryAgain()
    {
        TestFactory.AddService(Db);
        var limiter = Services.GetRequiredService<ActionRateLimiter>();
        while (limiter.TryAcquire(LimitedAction.CreateBooking)) { }

        var cut = RenderWizard();
        DriveToReview(cut);
        Forward(cut).Click();

        Assert.Empty(Db.Bookings);
        Assert.Contains("Too many bookings", Error(cut), StringComparison.Ordinal);
        // The limiter is checked before submitting is ever set, so the customer is left
        // on the review step with the button live and can retry once the window passes.
        Assert.False(Forward(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Confirm_RefusedAtTheLastMoment_ShowsWhy_AndLeavesTheFormUsable()
    {
        // The review screen can go stale: nothing stops the customer's other bookings
        // being made elsewhere while they sit on it, and the cap is only re-checked
        // inside CreateBooking. This is the path where submitting has already been set,
        // so it is the one that has to put it back — otherwise the button stays stuck
        // reading "Confirming…" and the booking can never be retried.
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        DriveToReview(cut);

        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            AddUpcomingBooking("jane@example.com");
        Db.SaveChanges();

        Forward(cut).Click();

        Assert.Equal(BookingService.MaxActiveBookingsPerEmail, Db.Bookings.Count());
        Assert.Contains("upcoming bookings", Error(cut), StringComparison.Ordinal);
        Assert.True(OnReviewStep(cut));
        Assert.False(Forward(cut).HasAttribute("disabled"));
    }

    // ----------------------------------------------------------------- photos

    private static InputFileContent Photo(string name, int sizeBytes) =>
        InputFileContent.CreateFromBinary(new byte[sizeBytes], name, contentType: "image/jpeg");

    [Fact]
    public void Photos_BeyondTheAllowedCount_AreRefusedAsABatch()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);

        var tooMany = Enumerable.Range(0, 6).Select(i => Photo($"p{i}.jpg", 16)).ToArray();
        cut.FindComponent<InputFile>().UploadFiles(tooMany);

        Assert.Contains("up to", Error(cut), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll(".photo-picker-remove"));
    }

    [Fact]
    public void Photos_OverTheTotalByteBudget_AreRefused_KeepingTheOnesThatFit()
    {
        // The cap is on the total held in server memory per form, not on any one file:
        // five 8MB photos per person filling in the form is how a small VPS runs out.
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);

        const int sixMb = 6 * 1024 * 1024;
        cut.FindComponent<InputFile>().UploadFiles(
            Photo("a.jpg", sixMb), Photo("b.jpg", sixMb), Photo("c.jpg", sixMb));

        Assert.Contains("MB in total", Error(cut), StringComparison.Ordinal);
        // The two that fitted are kept — the batch is not thrown away wholesale.
        Assert.Equal(2, cut.FindAll(".photo-picker-remove").Count);
    }

    [Fact]
    public void Photos_AreAttachedToTheBooking_OnConfirm()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);
        cut.FindComponent<InputFile>().UploadFiles(Photo("bike.jpg", 512));
        FillDetails(cut);
        Forward(cut).Click();
        Forward(cut).Click();

        Assert.Single(Db.Bookings);
        Assert.Single(Db.BookingPhotos);
    }

    [Fact]
    public void Photos_CanBeRemovedBeforeConfirming()
    {
        TestFactory.AddService(Db);
        var cut = RenderWizard();
        ChooseService(cut);
        ChooseSlot(cut);
        cut.FindComponent<InputFile>().UploadFiles(Photo("bike.jpg", 512));

        cut.Find(".photo-picker-remove").Click();

        Assert.Empty(cut.FindAll(".photo-picker-remove"));
    }

    // ------------------------------------------------------------- start over

    [Fact]
    public void StartOver_ClearsTheForm_ButKeepsASignedInCustomersDetails()
    {
        TestFactory.AddService(Db);
        var customer = TestFactory.AddCustomer(Db, fullName: "Ada Rider");

        var cut = RenderWizard(SignedInAs(customer));
        ChooseService(cut);
        ChooseSlot(cut);
        cut.Find("#booking-notes").Change("please true the rear wheel");

        cut.Find(".booking-nav .btn-ghost").Click();

        // Back at the beginning...
        Assert.NotEmpty(cut.FindAll(".service-pick-card"));

        // ...but still signed in, and still knowing who they are. Start over is a plain
        // button rather than a navigation, so without the re-prefill it would sign-in
        // strip a customer whose name and number the page already has.
        ChooseService(cut);
        ChooseSlot(cut);
        Assert.Equal("Ada Rider", ((IHtmlInputElement)cut.Find("#booking-name")).Value);
        Assert.Equal("", ((IHtmlTextAreaElement)cut.Find("#booking-notes")).Value);
    }
}
