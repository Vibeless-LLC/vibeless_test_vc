using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The 8 date functions of §7.6, on Excel's 1900 serial calendar (ruling R69). Registered by <see cref="Register"/>.
/// </summary>
/// <remarks>
/// Serial calendar: 1 = 1900-01-01, 59 = 1900-02-28, 60 = the fictitious 1900-02-29, 61 = 1900-03-01 and 0 = "1900-01-00";
/// valid serials are 0..2958465 (9999-12-31). From 61 on the serial is the proleptic Gregorian day count plus a constant
/// (<see cref="UnixEpochSerial"/>); before that the explicit mapping in <see cref="FromSerial"/> and
/// <see cref="MonthStart"/> applies, so the 1900 leap-year bug is visible in one place. All arithmetic is integer
/// (long) arithmetic: no DateTime, culture or time zone (§6.8). Arguments are coerced per §6.2 and numbers truncated
/// toward zero (§6.9); errors propagate in argument order (§6.4) before any range check. Omitted arguments (R38) are
/// #VALUE! except WEEKDAY's type, which defaults to 1.
/// </remarks>
internal static class DateFunctions
{
    private static readonly CellValue NumError = CellValue.FromError(ErrorCodes.Num);

    /// <summary>The serial of 1970-01-01 (Excel: 25569).</summary>
    private const long UnixEpochSerial = 25569;
    private const long MaxSerial = 2958465;

    public static void Register(BuiltInFunctionTable.Builder b)
    {
        Add(b, "DATE", 3, 3, Date);
        Add(b, "YEAR", 1, 1, a => Part(a, 0));
        Add(b, "MONTH", 1, 1, a => Part(a, 1));
        Add(b, "DAY", 1, 1, a => Part(a, 2));
        Add(b, "WEEKDAY", 1, 2, Weekday);
        Add(b, "EDATE", 2, 2, a => Shift(a, endOfMonth: false));
        Add(b, "EOMONTH", 2, 2, a => Shift(a, endOfMonth: true));
        Add(b, "DAYS", 2, 2, Days);
    }

    private static void Add(BuiltInFunctionTable.Builder b, string name, int min, int max, Func<IFunctionArguments, CellValue> body) =>
        b.Add(name, min, max, new DelegateFunction(body));

    // ---- calendar arithmetic (pure integer) ----

    private static long FloorDiv(long a, long b) => a / b - ((a % b != 0 && (a < 0) != (b < 0)) ? 1 : 0);

    private static long FloorMod(long a, long b) => a - FloorDiv(a, b) * b;

    private static bool IsLeap(long year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    /// <summary>Days in a month of the serial calendar: February 1900 has 29 days (R69).</summary>
    private static int DaysInMonth(long year, int month) => month switch
    {
        2 => (IsLeap(year) || year == 1900) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>Gregorian days since 1970-01-01 (days_from_civil), for any year.</summary>
    private static long DaysFromCivil(long year, int month, int day)
    {
        long y = month <= 2 ? year - 1 : year;
        long era = FloorDiv(y, 400);
        long yoe = y - era * 400;
        long doy = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    private static void CivilFromDays(long days, out long year, out int month, out int day)
    {
        long z = days + 719468;
        long era = FloorDiv(z, 146097);
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        day = (int)(doy - (153 * mp + 2) / 5 + 1);
        month = (int)(mp < 10 ? mp + 3 : mp - 9);
        year = yoe + era * 400 + (month <= 2 ? 1 : 0);
    }

    /// <summary>The serial of the first day of a month. Before 1900-03 the Gregorian count is one too high (the
    /// fictitious 1900-02-29 sits in between), so 1900-01-01 is 1, 1900-02-01 is 32 and 1900-03-01 is 61 (R69).</summary>
    private static long MonthStart(long year, int month)
    {
        long serial = DaysFromCivil(year, month, 1) + UnixEpochSerial;
        return (year < 1900 || (year == 1900 && month < 3)) ? serial - 1 : serial;
    }

    /// <summary>Splits a valid serial (0..<see cref="MaxSerial"/>) into year, month and day.</summary>
    private static void FromSerial(long serial, out long year, out int month, out int day)
    {
        if (serial >= 61) { CivilFromDays(serial - UnixEpochSerial, out year, out month, out day); return; }
        year = 1900;
        if (serial == 60) { month = 2; day = 29; }
        else if (serial <= 31) { month = 1; day = (int)serial; }   // 0 is "1900-01-00"
        else { month = 2; day = (int)(serial - 31); }
    }

    // ---- argument helpers ----

    /// <summary>Evaluates and coerces argument <paramref name="index"/>, then truncates toward zero (§6.9).</summary>
    private static bool TryInteger(IFunctionArguments a, int index, out long value, out CellValue error)
    {
        value = 0;
        if (!Coercion.TryToNumber(a.Evaluate(index), out double n, out error)) return false;
        value = (long)Math.Clamp(Math.Truncate(n), -1e15, 1e15);
        return true;
    }

    private static bool InSerialRange(long serial) => serial is >= 0 and <= MaxSerial;

    // ---- functions ----

    /// <summary>DATE(y,m,d): y 0..1899 adds 1900, y outside 0..9999 is #NUM!; month and day roll over arithmetically on the
    /// serial calendar (DATE(2024,-1,1) is 2023-11-01, DATE(1900,3,0) is 60). A month or day beyond the int range, or a
    /// result outside 0..2958465, is #NUM! (D-46).</summary>
    private static CellValue Date(IFunctionArguments a)
    {
        if (!TryInteger(a, 0, out long y, out var error)) return error;
        if (!TryInteger(a, 1, out long m, out error)) return error;
        if (!TryInteger(a, 2, out long d, out error)) return error;
        if (y < 0 || y >= 10000) return NumError;
        if (y <= 1899) y += 1900;
        if (m is < int.MinValue or > int.MaxValue || d is < int.MinValue or > int.MaxValue) return NumError;

        long months = y * 12 + (m - 1);
        long serial = MonthStart(FloorDiv(months, 12), (int)FloorMod(months, 12) + 1) + d - 1;
        return InSerialRange(serial) ? CellValue.FromNumber(serial) : NumError;
    }

    /// <summary>YEAR / MONTH / DAY: <paramref name="part"/> 0, 1, 2.</summary>
    private static CellValue Part(IFunctionArguments a, int part)
    {
        if (!TryInteger(a, 0, out long s, out var error)) return error;
        if (!InSerialRange(s)) return NumError;
        FromSerial(s, out long year, out int month, out int day);
        return CellValue.FromNumber(part == 0 ? year : part == 1 ? month : day);
    }

    /// <summary>WEEKDAY(s,[type=1]): serial 1 is a Sunday. The serial must be in 0..2958465 (#NUM!); an omitted type is 1
    /// and any type other than 1, 2, 3 (after truncation) is #NUM!.</summary>
    private static CellValue Weekday(IFunctionArguments a)
    {
        if (!TryInteger(a, 0, out long s, out var error)) return error;
        long type = 1;
        if (a.Count > 1 && !a.IsOmitted(1) && !TryInteger(a, 1, out type, out error)) return error;
        if (!InSerialRange(s) || type is < 1 or > 3) return NumError;
        return CellValue.FromNumber(type switch
        {
            1 => FloorMod(s - 1, 7) + 1,
            2 => FloorMod(s - 2, 7) + 1,
            _ => FloorMod(s - 2, 7),
        });
    }

    /// <summary>EDATE / EOMONTH. The serial is split like YEAR/MONTH/DAY (so for s &lt; 61 the serial calendar applies:
    /// 60 is 1900-02-29, 0 is day 0 of January 1900), <c>months</c> are added to the month index, and the day is clamped to
    /// the target month's length in the same calendar (February 1900 has 29 days); day 0 is kept for EDATE. A result
    /// outside 0..2958465 is #NUM! (D-46).</summary>
    private static CellValue Shift(IFunctionArguments a, bool endOfMonth)
    {
        if (!TryInteger(a, 0, out long s, out var error)) return error;
        if (!TryInteger(a, 1, out long months, out error)) return error;
        if (!InSerialRange(s)) return NumError;
        FromSerial(s, out long year, out int month, out int day);
        long total = year * 12 + (month - 1) + months;
        long targetYear = FloorDiv(total, 12);
        int targetMonth = (int)FloorMod(total, 12) + 1;
        if (targetYear is < 1800 or > 10100) return NumError;     // far outside the valid range; keeps the arithmetic small
        int length = DaysInMonth(targetYear, targetMonth);
        long serial = MonthStart(targetYear, targetMonth) + (endOfMonth ? length : Math.Min(day, length)) - 1;
        return InSerialRange(serial) ? CellValue.FromNumber(serial) : NumError;
    }

    /// <summary>DAYS(end,start) = INT(end) - INT(start); INT is floor, there is no range check (R69).</summary>
    private static CellValue Days(IFunctionArguments a)
    {
        if (!Coercion.TryToNumber(a.Evaluate(0), out double end, out var error)) return error;
        if (!Coercion.TryToNumber(a.Evaluate(1), out double start, out error)) return error;
        return CellValue.FromNumber(Math.Floor(end) - Math.Floor(start));
    }
}
