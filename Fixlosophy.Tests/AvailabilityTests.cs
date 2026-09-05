using Fixlosophy.Data;
using Fixlosophy.Services;

namespace Fixlosophy.Tests;

// Closures and staff absence decide what customers can book, so these pin down the
// rule from both ends: a closed day offers nothing, and an open day is unaffected.
public class AvailabilityTests
{
    private static Closure NewClosure(DateTime from, DateTime? to = null,
        string reason = "Bank holiday", string? startTime = null, string? endTime = null) => new()
        {
            StartDate = from.Date,
            EndDate = (to ?? from).Date,
            Reason = reason,
            StartTime = startTime,
            EndTime = endTime
        };

    // ── Closures ─────────────────────────────────────────────────────────────

    [Fact]
    public void AllDayClosure_ClosesTheDay()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday();
        db.Closures.Add(NewClosure(day, reason: "Christmas Day"));
        db.SaveChanges();

        var availability = TestFactory.NewAvailability(db);
        Assert.Equal(DayState.Closed, availability.StateOf(day));
        Assert.Equal("Christmas Day", availability.AllDayClosureOn(day)?.Reason);
        Assert.Empty(TestFactory.NewBookingService(db).GetAvailableSlots(day));
    }

    // Both ends inclusive — "closed the 24th to the 2nd" has to mean the 2nd as well.
    [Fact]
    public void ClosureRange_CoversBothEndsAndEverythingBetween()
    {
        using var db = TestFactory.NewDb();
        var start = TestFactory.FutureWorkday(10);
        var end = start.AddDays(4);
        db.Closures.Add(NewClosure(start, end, "Christmas break"));
        db.SaveChanges();

        var availability = TestFactory.NewAvailability(db);
        for (var d = start; d <= end; d = d.AddDays(1))
            Assert.Equal(DayState.Closed, availability.StateOf(d));

        Assert.Equal(DayState.Open, availability.StateOf(start.AddDays(-1)));
        Assert.Equal(DayState.Open, availability.StateOf(end.AddDays(1)));
    }

    // A part-day closure narrows the slot list without shutting the day — which is
    // the whole difference between "closing early on Saturday" and "shut Saturday".
    [Fact]
    public void PartDayClosure_TakesSomeSlotsAndLeavesTheDayOpen()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday();
        db.Closures.Add(NewClosure(day, reason: "Closing early", startTime: "14:00", endTime: "19:00"));
        db.SaveChanges();

        Assert.Equal(DayState.Open, TestFactory.NewAvailability(db).StateOf(day));

        var slots = TestFactory.NewBookingService(db).GetAvailableSlots(day);
        Assert.NotEmpty(slots);
        Assert.Contains("09:00", slots);
        Assert.DoesNotContain("14:00", slots);
        Assert.DoesNotContain("15:00", slots);
    }

    // Start-inclusive, end-exclusive, matching how closing time works elsewhere.
    [Theory]
    [InlineData("13:00", true)]
    [InlineData("14:00", true)]
    [InlineData("15:00", false)]  // the window's end is not itself closed
    public void PartDayClosure_BoundariesMatchTheTradingHoursConvention(string slot, bool blocked)
    {
        var closure = NewClosure(DateTime.Today, startTime: "13:00", endTime: "15:00");
        Assert.Equal(blocked, closure.CoversSlot(slot));
    }

    [Fact]
    public void AddClosure_RejectsABackwardsRange()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday();
        var (saved, error) = TestFactory.NewAvailability(db)
            .AddClosure(NewClosure(day.AddDays(3), day));

        Assert.Null(saved);
        Assert.NotNull(error);
    }

    [Fact]
    public void AddClosure_RejectsAMissingReason()
    {
        using var db = TestFactory.NewDb();
        var (saved, error) = TestFactory.NewAvailability(db)
            .AddClosure(NewClosure(TestFactory.FutureWorkday(), reason: "  "));

        Assert.Null(saved);
        Assert.NotNull(error);
    }

    // One time without the other is ambiguous — from 13:00 until when?
    [Fact]
    public void AddClosure_RejectsHalfATimeWindow()
    {
        using var db = TestFactory.NewDb();
        var (saved, error) = TestFactory.NewAvailability(db)
            .AddClosure(NewClosure(TestFactory.FutureWorkday(), startTime: "13:00"));

        Assert.Null(saved);
        Assert.NotNull(error);
    }

    // ── Staff absence ────────────────────────────────────────────────────────

    // The case the feature exists for: one mechanic, and they're off.
    [Fact]
    public void TheOnlyMechanicBeingAway_ClosesTheDay()
    {
        using var db = TestFactory.NewDb();
        var francesco = TestFactory.AddMechanic(db);
        var day = TestFactory.FutureWorkday();
        db.StaffAbsences.Add(new StaffAbsence
        {
            StaffId = francesco.Id, StartDate = day, EndDate = day, Type = AbsenceType.Holiday
        });
        db.SaveChanges();

        Assert.Equal(DayState.NoMechanic, TestFactory.NewAvailability(db).StateOf(day));
        Assert.Empty(TestFactory.NewBookingService(db).GetAvailableSlots(day));
    }

    // Two mechanics, one away: still open, and still one bike at a time. Capacity is
    // about the stand, not the headcount.
    [Fact]
    public void OneOfTwoMechanicsAway_LeavesTheDayOpenAtTheSameCapacity()
    {
        using var db = TestFactory.NewDb();
        var francesco = TestFactory.AddMechanic(db, "Francesco");
        TestFactory.AddMechanic(db, "Janek");
        var day = TestFactory.FutureWorkday();
        db.StaffAbsences.Add(new StaffAbsence
        {
            StaffId = francesco.Id, StartDate = day, EndDate = day, Type = AbsenceType.Holiday
        });
        db.SaveChanges();

        Assert.Equal(DayState.Open, TestFactory.NewAvailability(db).StateOf(day));
        Assert.NotEmpty(TestFactory.NewBookingService(db).GetAvailableSlots(day));
    }

    // Someone at a desk isn't someone at a stand.
    [Fact]
    public void NonMechanicStaffDoNotKeepTheDayOpen()
    {
        using var db = TestFactory.NewDb();
        var francesco = TestFactory.AddMechanic(db);
        db.Staff.Add(new StaffMember
        {
            FullName = "Office", Email = "office@example.com", IsActive = true, IsMechanic = false
        });
        var day = TestFactory.FutureWorkday();
        db.StaffAbsences.Add(new StaffAbsence { StaffId = francesco.Id, StartDate = day, EndDate = day });
        db.SaveChanges();

        Assert.Equal(DayState.NoMechanic, TestFactory.NewAvailability(db).StateOf(day));
    }

    // Failing safe: with nobody flagged, the rule is off rather than shutting the shop.
    // Unticking the last mechanic must not silently stop all bookings.
    [Fact]
    public void WithNoMechanicsConfigured_TheRuleIsInactive()
    {
        using var db = TestFactory.NewDb();
        var availability = TestFactory.NewAvailability(db);

        Assert.False(availability.MechanicRuleApplies);
        Assert.Equal(DayState.Open, availability.StateOf(TestFactory.FutureWorkday()));
    }

    [Fact]
    public void AnInactiveMechanicDoesNotCount()
    {
        using var db = TestFactory.NewDb();
        var francesco = TestFactory.AddMechanic(db);
        francesco.IsActive = false;
        db.SaveChanges();

        Assert.False(TestFactory.NewAvailability(db).MechanicRuleApplies);
    }

    // ── The booking horizon ──────────────────────────────────────────────────

    [Fact]
    public void BookingsAreRefusedBeyondTheHorizon()
    {
        using var db = TestFactory.NewDb();
        var tooFar = ShopClock.Today.Add(BookingService.BookingHorizon).AddDays(7);

        Assert.Empty(TestFactory.NewBookingService(db).GetAvailableSlots(tooFar));
        Assert.False(TestFactory.NewBookingService(db).IsDateAvailable(tooFar));
    }

    [Fact]
    public void BookingsAreAllowedUpToTheHorizon()
    {
        using var db = TestFactory.NewDb();
        var lastDay = BookingService.LatestBookableDate;
        while (lastDay.DayOfWeek == DayOfWeek.Sunday) lastDay = lastDay.AddDays(-1);

        Assert.NotEmpty(TestFactory.NewBookingService(db).GetAvailableSlots(lastDay));
    }

    // ── The server-side guard ────────────────────────────────────────────────
    // The slot list is a UI convenience; /book is a public POST target, and a closure
    // can land while somebody has the wizard open.

    [Fact]
    public void CreateBooking_RefusesAClosedDay()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday();
        db.Closures.Add(NewClosure(day, reason: "Staff training"));
        db.SaveChanges();

        var (booking, error) = TestFactory.NewBookingService(db).CreateBooking(NewBooking(day, "09:00"));

        Assert.Null(booking);
        Assert.Contains("Staff training", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBooking_RefusesASlotInsideAPartDayClosure()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday();
        db.Closures.Add(NewClosure(day, reason: "Delivery", startTime: "14:00", endTime: "19:00"));
        db.SaveChanges();

        var service = TestFactory.NewBookingService(db);
        Assert.Null(service.CreateBooking(NewBooking(day, "15:00")).booking);
        Assert.NotNull(service.CreateBooking(NewBooking(day, "09:00")).booking);
    }

    [Fact]
    public void CreateBooking_RefusesBeyondTheHorizon()
    {
        using var db = TestFactory.NewDb();
        var tooFar = ShopClock.Today.Add(BookingService.BookingHorizon).AddDays(7);
        while (tooFar.DayOfWeek == DayOfWeek.Sunday) tooFar = tooFar.AddDays(1);

        var (booking, error) = TestFactory.NewBookingService(db).CreateBooking(NewBooking(tooFar, "09:00"));

        Assert.Null(booking);
        Assert.NotNull(error);
    }

    // ── Orphan detection ─────────────────────────────────────────────────────

    // Closing a week without this strands whoever had booked into it — a locked door
    // and no word since the confirmation email.
    [Fact]
    public void FindAffectedBookings_ReturnsBookingsInsideTheRange()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        service.CreateBooking(NewBooking(day, "09:00", "jane@example.com"));
        service.CreateBooking(NewBooking(day.AddDays(1), "10:00", "bob@example.com"));
        service.CreateBooking(NewBooking(day.AddDays(30), "11:00", "far@example.com"));

        var affected = TestFactory.NewAvailability(db).FindAffectedBookings(day, day.AddDays(1));

        Assert.Equal(2, affected.Count);
        Assert.DoesNotContain(affected, b => b.CustomerEmail == "far@example.com");
    }

    [Fact]
    public void FindAffectedBookings_IgnoresBookingsAlreadyResolved()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        var (booking, _) = service.CreateBooking(NewBooking(day, "09:00"));
        booking!.Status = BookingStatus.Cancelled;
        db.SaveChanges();

        Assert.Empty(TestFactory.NewAvailability(db).FindAffectedBookings(day, day));
    }

    // A part-day closure only displaces the bookings inside its window.
    [Fact]
    public void FindAffectedBookings_RespectsAPartDayWindow()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        service.CreateBooking(NewBooking(day, "09:00", "morning@example.com"));
        service.CreateBooking(NewBooking(day, "15:00", "afternoon@example.com"));

        var affected = TestFactory.NewAvailability(db)
            .FindAffectedBookings(day, day, "14:00", "19:00");

        Assert.Single(affected);
        Assert.Equal("afternoon@example.com", affected[0].CustomerEmail);
    }

    // ── What the customer is told ────────────────────────────────────────────
    // "Closed — Christmas" and "fully booked" are different disappointments: one means
    // come back tomorrow, the other means try another time today. A grey square says
    // neither, which is what the booking calendar used to show.

    [Fact]
    public void DescribeMonth_CarriesTheClosureReasonToTheCustomer()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday(10);
        db.Closures.Add(NewClosure(day, reason: "Bank holiday"));
        db.SaveChanges();

        var month = TestFactory.NewBookingService(db).DescribeMonth(day.Year, day.Month);

        Assert.Equal(DayState.Closed, month[day].State);
        Assert.Equal("Closed — Bank holiday", month[day].CustomerLabel);
        Assert.False(month[day].IsBookable);
    }

    // Whose holiday it is isn't a customer's business — they get "Closed", full stop.
    [Fact]
    public void DescribeMonth_DoesNotLeakWhoIsAway()
    {
        using var db = TestFactory.NewDb();
        var francesco = TestFactory.AddMechanic(db, "Francesco");
        var day = TestFactory.FutureWorkday(10);
        db.StaffAbsences.Add(new StaffAbsence { StaffId = francesco.Id, StartDate = day, EndDate = day });
        db.SaveChanges();

        var month = TestFactory.NewBookingService(db).DescribeMonth(day.Year, day.Month);

        Assert.Equal(DayState.NoMechanic, month[day].State);
        Assert.Equal("Closed", month[day].CustomerLabel);
        Assert.Null(month[day].Reason);
    }

    // A day that's simply full gets no label — the calendar's own styling says that,
    // and "Closed" would be a lie.
    [Fact]
    public void DescribeMonth_GivesAFullDayNoLabel()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        foreach (var slot in BookingService.SlotsFor(day))
            service.CreateBooking(NewBooking(day, slot, $"{slot.Replace(":", "")}@example.com"));

        var month = service.DescribeMonth(day.Year, day.Month);

        Assert.Equal(DayState.Open, month[day].State);
        Assert.False(month[day].IsBookable);
        Assert.Null(month[day].CustomerLabel);
    }

    [Fact]
    public void DescribeMonth_GivesAnOrdinaryOpenDayNoLabel()
    {
        using var db = TestFactory.NewDb();
        var day = TestFactory.FutureWorkday(10);

        var month = TestFactory.NewBookingService(db).DescribeMonth(day.Year, day.Month);

        Assert.True(month[day].IsBookable);
        Assert.Null(month[day].CustomerLabel);
    }

    // ── The standing stranded list ───────────────────────────────────────────
    // FindAffectedBookings answers "what did the change I just made strand?";
    // FindStrandedBookings answers "what is stranded right now?". The admin screen
    // needs the second: the first version showed displaced bookings only in the moment
    // they were created, so switching tabs made them vanish with nothing left to say
    // they existed.

    [Fact]
    public void FindStrandedBookings_FindsBookingsOnAClosedDay()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        service.CreateBooking(NewBooking(day, "09:00"));
        db.Closures.Add(NewClosure(day, reason: "Closed"));
        db.SaveChanges();

        Assert.Single(TestFactory.NewAvailability(db).FindStrandedBookings());
    }

    [Fact]
    public void FindStrandedBookings_FindsBookingsOnADayWithNoMechanic()
    {
        using var db = TestFactory.NewDb();
        var francesco = TestFactory.AddMechanic(db);
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        service.CreateBooking(NewBooking(day, "09:00"));
        db.StaffAbsences.Add(new StaffAbsence { StaffId = francesco.Id, StartDate = day, EndDate = day });
        db.SaveChanges();

        Assert.Single(TestFactory.NewAvailability(db).FindStrandedBookings());
    }

    // A part-day closure strands only what falls inside its window.
    [Fact]
    public void FindStrandedBookings_FindsOnlySlotsInsideAPartDayClosure()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        service.CreateBooking(NewBooking(day, "09:00", "morning@example.com"));
        service.CreateBooking(NewBooking(day, "15:00", "afternoon@example.com"));
        db.Closures.Add(NewClosure(day, reason: "Delivery", startTime: "14:00", endTime: "19:00"));
        db.SaveChanges();

        var stranded = TestFactory.NewAvailability(db).FindStrandedBookings();

        Assert.Single(stranded);
        Assert.Equal("afternoon@example.com", stranded[0].CustomerEmail);
    }

    [Fact]
    public void FindStrandedBookings_IsEmptyWhenNothingIsClosed()
    {
        using var db = TestFactory.NewDb();
        TestFactory.NewBookingService(db).CreateBooking(NewBooking(TestFactory.FutureWorkday(10), "09:00"));

        Assert.Empty(TestFactory.NewAvailability(db).FindStrandedBookings());
    }

    // Dealing with one takes it off the list — that's what makes the warning clear
    // itself rather than nagging forever.
    [Fact]
    public void FindStrandedBookings_DropsOneOnceItIsCancelled()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var day = TestFactory.FutureWorkday(10);

        var (booking, _) = service.CreateBooking(NewBooking(day, "09:00"));
        db.Closures.Add(NewClosure(day, reason: "Closed"));
        db.SaveChanges();
        Assert.Single(TestFactory.NewAvailability(db).FindStrandedBookings());

        booking!.Status = BookingStatus.Cancelled;
        db.SaveChanges();

        Assert.Empty(TestFactory.NewAvailability(db).FindStrandedBookings());
    }

    [Fact]
    public void FindStrandedBookings_DropsOneOnceItIsMovedSomewhereOpen()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var closed = TestFactory.FutureWorkday(10);
        var open = TestFactory.FutureWorkday(17);

        var (booking, _) = service.CreateBooking(NewBooking(closed, "09:00"));
        db.Closures.Add(NewClosure(closed, reason: "Closed"));
        db.SaveChanges();
        Assert.Single(TestFactory.NewAvailability(db).FindStrandedBookings());

        service.RescheduleBooking(booking!.Id, open, "11:00");

        Assert.Empty(TestFactory.NewAvailability(db).FindStrandedBookings());
    }

    // Yesterday's closed day is history, not something to chase.
    [Fact]
    public void FindStrandedBookings_IgnoresThePast()
    {
        using var db = TestFactory.NewDb();
        var past = ShopClock.Today.AddDays(-7);
        db.Bookings.Add(new Booking
        {
            Reference = "FIX-OLD-001",
            CustomerName = "Jane Doe",
            CustomerEmail = "jane@example.com",
            SlotDate = past,
            SlotTime = "09:00",
            Status = BookingStatus.Confirmed
        });
        db.Closures.Add(NewClosure(past, reason: "Was closed"));
        db.SaveChanges();

        Assert.Empty(TestFactory.NewAvailability(db).FindStrandedBookings());
    }

    // ── Rescheduling ─────────────────────────────────────────────────────────

    [Fact]
    public void RescheduleBooking_MovesItAndKeepsTheReference()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var from = TestFactory.FutureWorkday(10);
        var to = TestFactory.FutureWorkday(17);

        var (booking, _) = service.CreateBooking(NewBooking(from, "09:00"));
        var reference = booking!.Reference;

        var (moved, error) = service.RescheduleBooking(booking.Id, to, "11:00");

        Assert.Null(error);
        Assert.Equal(reference, moved!.Reference);
        Assert.Equal(to.Date, moved.SlotDate.Date);
        Assert.Equal("11:00", moved.SlotTime);
    }

    // The reminder that already went out named the old date, so they're owed another.
    [Fact]
    public void RescheduleBooking_ClearsTheReminderStamp()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var from = TestFactory.FutureWorkday(10);

        var (booking, _) = service.CreateBooking(NewBooking(from, "09:00"));
        booking!.ReminderSentAt = ShopClock.Now;
        db.SaveChanges();

        service.RescheduleBooking(booking.Id, TestFactory.FutureWorkday(17), "11:00");

        Assert.Null(db.Bookings.Find(booking.Id)!.ReminderSentAt);
    }

    [Fact]
    public void RescheduleBooking_RefusesATakenSlot()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var from = TestFactory.FutureWorkday(10);
        var to = TestFactory.FutureWorkday(17);

        var (mine, _) = service.CreateBooking(NewBooking(from, "09:00", "mine@example.com"));
        service.CreateBooking(NewBooking(to, "11:00", "theirs@example.com"));

        var (moved, error) = service.RescheduleBooking(mine!.Id, to, "11:00");

        Assert.Null(moved);
        Assert.NotNull(error);
    }

    [Fact]
    public void RescheduleBooking_RefusesAClosedDay()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var from = TestFactory.FutureWorkday(10);
        var to = TestFactory.FutureWorkday(17);
        db.Closures.Add(NewClosure(to, reason: "Closed"));
        db.SaveChanges();

        var (booking, _) = service.CreateBooking(NewBooking(from, "09:00"));
        var (moved, error) = service.RescheduleBooking(booking!.Id, to, "11:00");

        Assert.Null(moved);
        Assert.NotNull(error);
    }

    [Fact]
    public void RescheduleBooking_RefusesAClosedBooking()
    {
        using var db = TestFactory.NewDb();
        var service = TestFactory.NewBookingService(db);
        var (booking, _) = service.CreateBooking(NewBooking(TestFactory.FutureWorkday(10), "09:00"));
        booking!.Status = BookingStatus.Completed;
        db.SaveChanges();

        var (moved, error) = service.RescheduleBooking(booking.Id, TestFactory.FutureWorkday(17), "11:00");

        Assert.Null(moved);
        Assert.NotNull(error);
    }

    private static Booking NewBooking(DateTime date, string slot, string email = "jane@example.com") => new()
    {
        CustomerName = "Jane Doe",
        CustomerEmail = email,
        CustomerPhone = "07700 900000",
        ServiceCategory = "Servicing Packages",
        ServiceName = "Full Service",
        ServicePrice = 70m,
        SlotDate = date.Date,
        SlotTime = slot,
        BikeDescription = "Trek FX3"
    };
}
