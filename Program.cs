using Fixlosophy.Components;
using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

// Allow DateTime.Now / DateTime.Today to be stored as Postgres "timestamp without time zone"
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<InflationService>();
builder.Services.AddSingleton<GoogleCalendarService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db        = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config    = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var inflation = scope.ServiceProvider.GetRequiredService<InflationService>();
    EnsureSchema(db);
    SeedServicePricings(db);
    SeedDemoData(db);
    SeedDefaultAdmin(db);
    await ApplyAnnualPriceIncreaseAsync(db, config, inflation);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void EnsureSchema(AppDbContext db)
{
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""Bookings"" (
            ""Id""              varchar(36)   NOT NULL,
            ""Reference""       text          NOT NULL DEFAULT '',
            ""CreatedAt""       timestamp     NOT NULL DEFAULT now(),
            ""CustomerName""    text          NOT NULL DEFAULT '',
            ""CustomerEmail""   text          NOT NULL DEFAULT '',
            ""CustomerPhone""   text          NOT NULL DEFAULT '',
            ""ServiceCategory"" text          NOT NULL DEFAULT '',
            ""ServiceName""     text          NOT NULL DEFAULT '',
            ""ServicePrice""    numeric(18,2) NOT NULL DEFAULT 0,
            ""SlotDate""        timestamp     NOT NULL DEFAULT now(),
            ""SlotTime""        text          NOT NULL DEFAULT '',
            ""BikeDescription"" text          NOT NULL DEFAULT '',
            ""Notes""           text          NOT NULL DEFAULT '',
            ""Status""          integer       NOT NULL DEFAULT 0,
            CONSTRAINT ""PK_Bookings"" PRIMARY KEY (""Id"")
        );

        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""CustomerId""       varchar(36) NULL;
        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AssignedStaffId""  varchar(36) NULL;
        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""CalendarEventId""  text        NULL;

        -- Relational FK constraints (idempotent via pg_constraint check)
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Bookings_Customers') THEN
                ALTER TABLE ""Bookings"" ADD CONSTRAINT ""FK_Bookings_Customers""
                    FOREIGN KEY (""CustomerId"") REFERENCES ""Customers""(""Id"") ON DELETE SET NULL;
            END IF;
        END $$;

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Bookings_Staff') THEN
                ALTER TABLE ""Bookings"" ADD CONSTRAINT ""FK_Bookings_Staff""
                    FOREIGN KEY (""AssignedStaffId"") REFERENCES ""Staff""(""Id"") ON DELETE SET NULL;
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS ""Customers"" (
            ""Id""           varchar(36) NOT NULL,
            ""Email""        text        NOT NULL DEFAULT '',
            ""FullName""     text        NOT NULL DEFAULT '',
            ""Phone""        text        NOT NULL DEFAULT '',
            ""PasswordHash"" text        NOT NULL DEFAULT '',
            ""CreatedAt""    timestamp   NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_Customers"" PRIMARY KEY (""Id"")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_Email"" ON ""Customers"" (""Email"");

        CREATE TABLE IF NOT EXISTS ""Staff"" (
            ""Id""                     varchar(36) NOT NULL,
            ""FullName""               text        NOT NULL DEFAULT '',
            ""Email""                  text        NOT NULL DEFAULT '',
            ""PasswordHash""           text        NOT NULL DEFAULT '',
            ""Role""                   integer     NOT NULL DEFAULT 1,
            ""IsActive""               boolean     NOT NULL DEFAULT true,
            ""CreatedAt""              timestamp   NOT NULL DEFAULT now(),
            ""CanViewAllBookings""     boolean     NOT NULL DEFAULT false,
            ""CanManageBookings""      boolean     NOT NULL DEFAULT true,
            ""CanViewCustomerDetails"" boolean     NOT NULL DEFAULT false,
            CONSTRAINT ""PK_Staff"" PRIMARY KEY (""Id"")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Staff_Email"" ON ""Staff"" (""Email"");

        CREATE TABLE IF NOT EXISTS ""ServicePricings"" (
            ""Id""           varchar(36)   NOT NULL,
            ""Name""         text          NOT NULL DEFAULT '',
            ""Category""     text          NOT NULL DEFAULT '',
            ""CurrentPrice"" numeric(18,2) NOT NULL DEFAULT 0,
            ""Duration""     text          NOT NULL DEFAULT '',
            ""Icon""         text          NOT NULL DEFAULT '',
            ""SortOrder""    integer       NOT NULL DEFAULT 0,
            ""IsQuoteOnly""  boolean       NOT NULL DEFAULT false,
            CONSTRAINT ""PK_ServicePricings"" PRIMARY KEY (""Id"")
        );

        CREATE TABLE IF NOT EXISTS ""PriceAdjustments"" (
            ""Id""        serial       NOT NULL,
            ""Year""      integer      NOT NULL,
            ""Rate""      numeric(8,4) NOT NULL,
            ""AppliedAt"" timestamp    NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_PriceAdjustments"" PRIMARY KEY (""Id"")
        );

        -- Enable RLS on all public tables to prevent exposure via PostgREST.
        -- The app connects as postgres (superuser) which bypasses RLS, so this
        -- only affects the anon / authenticated roles used by the REST API.
        ALTER TABLE ""Bookings""         ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""Customers""        ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""Staff""            ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""ServicePricings""  ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""PriceAdjustments"" ENABLE ROW LEVEL SECURITY;

        -- ServicePricings is a public price catalogue; allow anonymous reads.
        -- All other tables have no policies, so PostgREST access is fully blocked.
        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_policies
                WHERE schemaname = 'public'
                  AND tablename  = 'ServicePricings'
                  AND policyname = 'anon_read_service_pricings'
            ) THEN
                CREATE POLICY anon_read_service_pricings ON ""ServicePricings""
                    FOR SELECT TO anon, authenticated USING (true);
            END IF;
        END $$;
    ");
}

static void SeedServicePricings(AppDbContext db)
{
    if (db.ServicePricings.Any()) return;

    ServicePricing[] services =
    [
        new() { Name = "Basic Service",                   Category = "Servicing Packages", CurrentPrice = 35,  Duration = "1 hour",    Icon = "&#128736;", SortOrder = 10 },
        new() { Name = "Full Service",                    Category = "Servicing Packages", CurrentPrice = 70,  Duration = "2–3 hours", Icon = "&#128736;", SortOrder = 20 },
        new() { Name = "Gold Service",                    Category = "Servicing Packages", CurrentPrice = 120, Duration = "3–4 hours", Icon = "&#128736;", SortOrder = 30 },
        new() { Name = "Brompton Full Service",           Category = "Specialist",         CurrentPrice = 70,  Duration = "2–3 hours", Icon = "&#9889;",   SortOrder = 40 },
        new() { Name = "Fixed-Single Speed Full Service", Category = "Specialist",         CurrentPrice = 50,  Duration = "2 hours",   Icon = "&#9889;",   SortOrder = 50 },
        new() { Name = "Kids Bike Basic Service",         Category = "Specialist",         CurrentPrice = 25,  Duration = "1 hour",    Icon = "&#9889;",   SortOrder = 60 },
        new() { Name = "Bottom Bracket Service",          Category = "Components",         CurrentPrice = 25,  Duration = "1 hour",    Icon = "&#128295;", SortOrder = 70 },
        new() { Name = "Wheel Build",                     Category = "Components",         CurrentPrice = 50,  Duration = "2–3 hours", Icon = "&#128295;", SortOrder = 80 },
        new() { Name = "Wheel Trueing",                   Category = "Components",         CurrentPrice = 15,  Duration = "30 min",    Icon = "&#128295;", SortOrder = 90 },
        new() { Name = "Headset Service",                 Category = "Components",         CurrentPrice = 25,  Duration = "45 min",    Icon = "&#128295;", SortOrder = 100 },
        new() { Name = "Hub Service",                     Category = "Components",         CurrentPrice = 15,  Duration = "45 min",    Icon = "&#128295;", SortOrder = 110 },
        new() { Name = "Gear Service",                    Category = "Components",         CurrentPrice = 10,  Duration = "30 min",    Icon = "&#128295;", SortOrder = 120 },
    ];

    foreach (var s in services)
        db.ServicePricings.Add(s);
    db.SaveChanges();
}

static async Task ApplyAnnualPriceIncreaseAsync(
    AppDbContext db, IConfiguration config, InflationService inflation)
{
    var today  = DateTime.Today;
    var april1 = new DateTime(today.Year, 4, 1);

    if (today < april1) return;
    if (db.PriceAdjustments.Any(a => a.Year == today.Year)) return;

    // Fetch live UK CPI (ONS CPIH L55O series); fall back to configured minimum
    var liveRate       = await inflation.GetLatestAnnualRateAsync();
    var configuredMin  = config.GetValue<decimal>("PriceIncrease:InflationRate", 0.03m);
    var rate           = Math.Max(liveRate ?? configuredMin, 0.05m);

    foreach (var service in db.ServicePricings.Where(s => !s.IsQuoteOnly).ToList())
        service.CurrentPrice = Math.Ceiling(service.CurrentPrice * (1 + rate));

    db.PriceAdjustments.Add(new PriceAdjustment { Year = today.Year, Rate = rate });
    db.SaveChanges();
}

static void SeedDemoData(AppDbContext db)
{
    if (db.Bookings.Any()) return;

    var today    = DateTime.Today;
    var names    = new[] { "Lena Fischer", "Marco Rossi", "Sarah O'Brien", "Tom Walsh", "Priya Nair" };
    var services = new[] { ("Servicing Packages", "Basic Service", 35m), ("Components", "Wheel Trueing", 15m), ("Servicing Packages", "Full Service", 70m), ("Specialist", "Brompton Full Service", 70m), ("Components", "Gear Service", 10m) };
    var emails   = new[] { "lena@example.com", "marco@example.com", "sarah@example.com", "tom@example.com", "priya@example.com" };
    var statuses = new[] { BookingStatus.Confirmed, BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.Pending, BookingStatus.Completed };
    var offsets  = new[] { 0, 0, 1, 2, -1 };
    var times    = new[] { "09:00", "11:00", "14:00", "10:00", "15:00" };
    var bikes    = new[] { "Trek FX3 Disc 2022", "Giant Escape 3", "Specialized Turbo Como", "Orbea MX 50", "Cannondale Quick 5" };

    for (int i = 0; i < names.Length; i++)
    {
        var date = today.AddDays(offsets[i]);
        while (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);
        db.Bookings.Add(new Booking
        {
            Reference       = $"FIX-{date:yyMMdd}-{i:D3}",
            CreatedAt       = DateTime.Now.AddHours(-i * 3),
            CustomerName    = names[i],
            CustomerEmail   = emails[i],
            CustomerPhone   = $"+1 555 00{i}00{i}",
            ServiceCategory = services[i].Item1,
            ServiceName     = services[i].Item2,
            ServicePrice    = services[i].Item3,
            SlotDate        = date,
            SlotTime        = times[i],
            BikeDescription = bikes[i],
            Notes           = "",
            Status          = statuses[i]
        });
    }
    db.SaveChanges();
}

static void SeedDefaultAdmin(AppDbContext db)
{
    if (db.Staff.Any()) return;

    db.Staff.Add(new StaffMember
    {
        FullName     = "Admin",
        Email        = "admin@fixlosophy.com",
        PasswordHash = AuthService.HashPassword("fixlosophy"),
        Role         = StaffRole.Admin,
        IsActive     = true
    });
    db.SaveChanges();
}
