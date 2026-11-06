using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Workflow;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Hr;

/// <summary>Demo organisation of Pulse Teknoloji: departments, staff, leave, holidays, shifts and two months of attendance.</summary>
public sealed class HrSeed(AppDbContext db, NumberingService numbering, WorkflowService workflow) : ISeedStep
{
    public int Order => 10;

    private static readonly (string Code, string Name, string? Parent)[] Departments =
    [
        ("YON", "Yönetim", null), ("BT", "Bilgi Teknolojileri", "YON"), ("IK", "İnsan Kaynakları", "YON"),
        ("FIN", "Finans", "YON"), ("SAT", "Satış", "YON"), ("OPS", "Operasyon", "YON"),
    ];

    private static readonly (string Code, string Name, string Department)[] Positions =
    [
        ("GM", "Genel Müdür", "YON"), ("BT-MDR", "BT Müdürü", "BT"), ("YAZ", "Yazılım Geliştirici", "BT"), ("SIS", "Sistem Yöneticisi", "BT"),
        ("IK-MDR", "İK Müdürü", "IK"), ("IK-UZM", "İK Uzmanı", "IK"), ("FIN-MDR", "Finans Müdürü", "FIN"), ("MUH", "Muhasebe Uzmanı", "FIN"),
        ("SAT-MDR", "Satış Müdürü", "SAT"), ("SAT-TEM", "Satış Temsilcisi", "SAT"), ("OPS-MDR", "Operasyon Müdürü", "OPS"), ("LOJ", "Lojistik Uzmanı", "OPS"),
    ];

    /// <summary>The first one heads the company.</summary>
    public static readonly (string Name, string Department, string Position, string? Manager, string HireDate, Gender Gender)[] Employees =
    [
        ("Hakan Erdem", "YON", "GM", null, "2015-01-05", Gender.MALE),
        ("Deniz Aksoy", "BT", "BT-MDR", "Hakan Erdem", "2018-03-12", Gender.FEMALE),
        ("Ayşe Yılmaz", "IK", "IK-MDR", "Hakan Erdem", "2017-06-01", Gender.FEMALE),
        ("Mehmet Kaya", "FIN", "FIN-MDR", "Hakan Erdem", "2016-09-19", Gender.MALE),
        ("Zeynep Demir", "SAT", "SAT-MDR", "Hakan Erdem", "2019-02-04", Gender.FEMALE),
        ("Mustafa Çelik", "OPS", "OPS-MDR", "Hakan Erdem", "2018-11-15", Gender.MALE),
        ("Kerem Polat", "BT", "YAZ", "Deniz Aksoy", "2022-04-18", Gender.MALE),
        ("Elif Şahin", "BT", "YAZ", "Deniz Aksoy", "2023-01-09", Gender.FEMALE),
        ("Ahmet Yıldız", "BT", "SIS", "Deniz Aksoy", "2021-07-01", Gender.MALE),
        ("Fatma Öztürk", "IK", "IK-UZM", "Ayşe Yılmaz", "2020-10-12", Gender.FEMALE),
        ("Emre Aydın", "FIN", "MUH", "Mehmet Kaya", "2022-08-22", Gender.MALE),
        ("Selin Arslan", "SAT", "SAT-TEM", "Zeynep Demir", "2023-05-15", Gender.FEMALE),
        ("Burak Doğan", "SAT", "SAT-TEM", "Zeynep Demir", "2024-02-01", Gender.MALE),
        ("Merve Koç", "OPS", "LOJ", "Mustafa Çelik", "2021-03-29", Gender.FEMALE),
        ("Can Kurt", "OPS", "LOJ", "Mustafa Çelik", "2025-06-02", Gender.MALE),
    ];

    private static readonly (string Code, string Name, int Days, bool Paid, bool Balance, string Color)[] LeaveTypes =
    [
        ("YILLIK", "Yıllık İzin", 14, true, true, "#6366f1"), ("MAZERET", "Mazeret İzni", 5, true, true, "#f59e0b"),
        ("HASTALIK", "Hastalık İzni", 0, true, false, "#ef4444"), ("UCRETSIZ", "Ücretsiz İzin", 0, false, false, "#64748b"),
    ];

    private static readonly (string Day, string Name)[] FixedHolidays =
    [
        ("01-01", "Yılbaşı"), ("04-23", "Ulusal Egemenlik ve Çocuk Bayramı"), ("05-01", "Emek ve Dayanışma Günü"),
        ("05-19", "Atatürk'ü Anma, Gençlik ve Spor Bayramı"), ("07-15", "Demokrasi ve Milli Birlik Günü"), ("08-30", "Zafer Bayramı"), ("10-29", "Cumhuriyet Bayramı"),
    ];

    /// <summary>Religious holidays follow the lunar calendar, so they are listed per year.</summary>
    private static readonly Dictionary<int, (string Date, string Name)[]> ReligiousHolidays = new()
    {
        [2026] = [("2026-03-20", "Ramazan Bayramı 1. Gün"), ("2026-03-21", "Ramazan Bayramı 2. Gün"), ("2026-03-22", "Ramazan Bayramı 3. Gün"),
            ("2026-05-27", "Kurban Bayramı 1. Gün"), ("2026-05-28", "Kurban Bayramı 2. Gün"), ("2026-05-29", "Kurban Bayramı 3. Gün"), ("2026-05-30", "Kurban Bayramı 4. Gün")],
        [2027] = [("2027-03-09", "Ramazan Bayramı 1. Gün"), ("2027-03-10", "Ramazan Bayramı 2. Gün"), ("2027-03-11", "Ramazan Bayramı 3. Gün"),
            ("2027-05-16", "Kurban Bayramı 1. Gün"), ("2027-05-17", "Kurban Bayramı 2. Gün"), ("2027-05-18", "Kurban Bayramı 3. Gün"), ("2027-05-19", "Kurban Bayramı 4. Gün")],
    };

    private static readonly (string Code, string Name, string Start, string End, int Break, string Color)[] Shifts =
    [
        ("GND", "Gündüz", "09:00", "18:00", 60, "#6366f1"), ("SBH", "Sabah", "07:00", "15:00", 30, "#0ea5e9"),
        ("AKS", "Akşam", "15:00", "23:00", 30, "#f59e0b"), ("GCE", "Gece", "23:00", "07:00", 30, "#64748b"),
    ];

    /// <summary>Europe/Istanbul has no daylight saving time.</summary>
    private const int Offset = 180;

    public async Task RunAsync(SeedContext ctx)
    {
        var year = DateTime.UtcNow.Year;
        var departments = new Dictionary<string, Guid>();
        foreach (var (code, name, parent) in Departments)
        {
            var d = await db.Departments.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.CompanyId == ctx.PulseId && x.Code == code);
            if (d == null)
            {
                d = new Department { Code = code, Name = name, ParentId = parent == null ? null : departments[parent] };
                db.Departments.Add(d);
                await db.SaveChangesAsync();
            }
            departments[code] = d.Id;
        }

        var positions = new Dictionary<string, Guid>();
        foreach (var (code, name, department) in Positions)
        {
            var p = await db.Positions.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.CompanyId == ctx.PulseId && x.Code == code);
            if (p == null)
            {
                p = new Position { Code = code, Name = name, DepartmentId = departments[department] };
                db.Positions.Add(p);
                await db.SaveChangesAsync();
            }
            positions[code] = p.Id;
        }

        var employees = new Dictionary<string, Employee>();
        var index = 0;
        foreach (var (fullName, department, position, manager, hireDate, gender) in Employees)
        {
            index++;
            var first = fullName[..fullName.IndexOf(' ')];
            var last = fullName[(fullName.IndexOf(' ') + 1)..];
            var e = await db.Employees.FirstOrDefaultAsync(x => x.FirstName == first && x.LastName == last);
            if (e == null)
            {
                var userId = ctx.UsersByName.TryGetValue(fullName, out var u) ? u : (Guid?)null;
                var email = userId == null ? $"{DataSeeder.Slug(first)}@pulse.example" : await db.Users.Where(x => x.Id == userId).Select(x => x.Email).FirstAsync();
                e = new Employee
                {
                    EmployeeNo = await numbering.NextAsync("EMPLOYEE"),
                    FirstName = first, LastName = last, Gender = gender, UserId = userId, BranchId = ctx.HqBranchId, Email = email,
                    Phone = $"+90 532 000 {index:0000}", HireDate = DateOnly.Parse(hireDate),
                    DepartmentId = departments[department], PositionId = positions[position],
                    ManagerId = manager == null ? null : employees[manager].Id,
                };
                db.Employees.Add(e);
                await db.SaveChangesAsync();
            }
            employees[fullName] = e;
        }
        foreach (var (fullName, department, position, _, _, _) in Employees.Where(x => x.Position == "GM" || x.Position.EndsWith("-MDR")))
        {
            var id = employees[fullName].Id;
            await db.Departments.Where(d => d.Code == department && d.ManagerId == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.ManagerId, id));
        }

        var leaveTypes = new Dictionary<string, Guid>();
        foreach (var t in LeaveTypes)
        {
            var row = await db.LeaveTypes.FirstOrDefaultAsync(x => x.Code == t.Code);
            if (row == null)
            {
                row = new LeaveType { Code = t.Code, Name = t.Name, DefaultDays = t.Days, IsPaid = t.Paid, RequiresBalance = t.Balance, Color = t.Color };
                db.LeaveTypes.Add(row);
                await db.SaveChangesAsync();
            }
            leaveTypes[t.Code] = row.Id;
        }
        foreach (var e in employees.Values)
        {
            foreach (var (code, days) in new[] { ("YILLIK", WorkingDays.AnnualLeaveDays(e.HireDate, year)), ("MAZERET", 5) })
            {
                if (!await db.LeaveBalances.AnyAsync(b => b.EmployeeId == e.Id && b.LeaveTypeId == leaveTypes[code] && b.Year == year))
                {
                    db.LeaveBalances.Add(new LeaveBalance { EmployeeId = e.Id, LeaveTypeId = leaveTypes[code], Year = year, EntitledDays = days });
                }
            }
        }
        await db.SaveChangesAsync();

        var holidays = new[] { year, year + 1 }.SelectMany(y => FixedHolidays.Select(h => (Date: DateOnly.Parse($"{y}-{h.Day}"), h.Name)))
            .Concat(new[] { year, year + 1 }.SelectMany(y => ReligiousHolidays.GetValueOrDefault(y) ?? []).Select(h => (Date: DateOnly.Parse(h.Date), h.Name)))
            .DistinctBy(h => h.Date).ToList();
        var existingHolidays = (await db.Holidays.Select(h => h.Date).ToListAsync()).ToHashSet();
        foreach (var h in holidays.Where(h => !existingHolidays.Contains(h.Date)))
        {
            db.Holidays.Add(new Holiday { Date = h.Date, Name = h.Name });
        }
        await db.SaveChangesAsync();

        var shifts = new Dictionary<string, Shift>();
        foreach (var s in Shifts)
        {
            var row = await db.Shifts.FirstOrDefaultAsync(x => x.Code == s.Code);
            if (row == null)
            {
                row = new Shift { Code = s.Code, Name = s.Name, StartTime = s.Start, EndTime = s.End, BreakMinutes = s.Break, Color = s.Color };
                db.Shifts.Add(row);
                await db.SaveChangesAsync();
            }
            shifts[s.Code] = row;
        }

        await SeedLeaveRequestsAsync(ctx, employees, leaveTypes, holidays.Select(h => h.Date).ToHashSet(), year);
        await SeedAttendanceAsync(employees.Values.ToList(), shifts, departments["OPS"]);
    }

    private async Task SeedLeaveRequestsAsync(SeedContext ctx, Dictionary<string, Employee> employees, Dictionary<string, Guid> types, HashSet<DateOnly> holidays, int year)
    {
        if (await db.LeaveRequests.AnyAsync()) return;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var nextMonday = today.AddDays(14);
        while (nextMonday.DayOfWeek != DayOfWeek.Monday) nextMonday = nextMonday.AddDays(1);
        if (nextMonday.Year != year) nextMonday = new DateOnly(year, 12, 21);
        (string Employee, string Type, DateOnly Start, DateOnly End, string Reason, bool Approved)[] samples =
        [
            ("Elif Şahin", "YILLIK", new DateOnly(year, 7, 13), new DateOnly(year, 7, 17), "Yaz tatili", true),
            ("Can Kurt", "HASTALIK", new DateOnly(year, 2, 9), new DateOnly(year, 2, 10), "Doktor raporu", true),
            ("Kerem Polat", "YILLIK", nextMonday, nextMonday.AddDays(4), "Aile ziyareti", false),
        ];
        foreach (var s in samples)
        {
            var employee = employees[s.Employee];
            var requester = ctx.UsersByName[s.Employee];
            var days = WorkingDays.Count(s.Start, s.End, holidays);
            var request = new LeaveRequest
            {
                Number = await numbering.NextAsync("LEAVE_REQUEST"),
                EmployeeId = employee.Id, LeaveTypeId = types[s.Type], StartDate = s.Start, EndDate = s.End, Days = days, Reason = s.Reason,
                Status = s.Approved ? LeaveRequestStatus.APPROVED : LeaveRequestStatus.PENDING, CreatedById = requester,
                DecidedAt = s.Approved ? DateTime.UtcNow : null,
            };
            db.LeaveRequests.Add(request);
            await db.SaveChangesAsync();
            if (s.Approved)
            {
                await db.LeaveBalances.Where(b => b.EmployeeId == employee.Id && b.LeaveTypeId == request.LeaveTypeId && b.Year == year)
                    .ExecuteUpdateAsync(x => x.SetProperty(b => b.UsedDays, b => b.UsedDays + days));
                continue;
            }
            using (db.User.ActAs(ctx.PulseId, requester))
            {
                var instance = await workflow.StartAsync(new StartWorkflowRequest(LeaveService.WorkflowCode, request.Id.ToString(),
                    $"{s.Employee} — Yıllık İzin ({days} gün)"));
                request.WorkflowInstanceId = instance.Id;
                await db.SaveChangesAsync();
            }
        }
    }

    /// <summary>Deterministic pseudo-random number in [0, 100) so re-seeding yields the same data.</summary>
    private static double Noise(double a, double b) => Math.Abs(Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453) % 100;

    /// <summary>
    /// A morning / evening rotation for operations and attendance from the start of the previous month until yesterday
    /// (some late arrivals, absences and overtime). Re-running tops up the days since the previous run.
    /// </summary>
    private async Task SeedAttendanceAsync(List<Employee> employees, Dictionary<string, Shift> shifts, Guid opsId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(Offset));
        var from = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var planEnd = today.AddDays(13);
        var holidays = (await db.Holidays.Where(h => h.Date >= from && h.Date <= today).Select(h => h.Date).ToListAsync()).ToHashSet();
        var leaves = await db.LeaveRequests.Where(l => l.Status == LeaveRequestStatus.APPROVED && l.EndDate >= from)
            .Select(l => new { l.EmployeeId, l.StartDate, l.EndDate }).ToListAsync();
        var planned = (await db.ShiftAssignments.Where(a => a.Date >= from).Select(a => new { a.EmployeeId, a.Date }).ToListAsync())
            .Select(x => (x.EmployeeId, x.Date)).ToHashSet();
        var recorded = (await db.AttendanceRecords.Where(a => a.Date >= from).Select(a => new { a.EmployeeId, a.Date }).ToListAsync())
            .Select(x => (x.EmployeeId, x.Date)).ToHashSet();

        var ordered = employees.Where(e => e.Status == EmployeeStatus.ACTIVE).OrderBy(e => e.EmployeeNo).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var rotates = e.DepartmentId == opsId;
            for (var date = from; date <= planEnd; date = date.AddDays(1))
            {
                if (WorkingDays.IsWeekend(date)) continue;
                var dayNumber = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Subtract(DateTime.UnixEpoch).TotalDays;
                var shift = Timesheet.DefaultShift;
                if (rotates)
                {
                    var week = (int)Math.Floor((dayNumber + 3) / 7);
                    var s = shifts[(week + i) % 2 == 0 ? "SBH" : "AKS"];
                    if (planned.Add((e.Id, date))) db.ShiftAssignments.Add(new ShiftAssignment { EmployeeId = e.Id, Date = date, ShiftId = s.Id });
                    shift = new ShiftTimes(s.Code, s.StartTime, s.EndTime, s.BreakMinutes);
                }
                if (date >= today || date < e.HireDate || holidays.Contains(date)) continue;
                if (leaves.Any(l => l.EmployeeId == e.Id && l.StartDate <= date && date <= l.EndDate)) continue;
                var roll = Noise(i + 1, dayNumber);
                if (roll < 3 || !recorded.Add((e.Id, date))) continue;
                var (start, end) = Timesheet.Window(shift);
                var checkIn = roll < 13 ? start + 8 + (int)Math.Round(roll * 2) : start - (int)Math.Round(roll % 15);
                var checkOut = roll > 85 ? end + 45 + (int)Math.Round(roll % 60) : end + (int)Math.Round(roll % 12);
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    EmployeeId = e.Id, Date = date, Source = AttendanceSource.SELF,
                    CheckIn = Timesheet.InstantOf(date, checkIn, Offset), CheckOut = Timesheet.InstantOf(date, checkOut, Offset),
                });
            }
        }
        await db.SaveChangesAsync();
    }
}
