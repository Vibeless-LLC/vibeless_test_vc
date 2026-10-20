using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.6 (DATE, YEAR, MONTH, DAY, WEEKDAY, EDATE, EOMONTH, DAYS), §6.2, §6.4, §6.8, §6.9, §5.4, D-46, R38, R69.
public class DateFunctionTests
{
    private static readonly CellValue Num = E(ErrorCodes.Num);
    private static readonly CellValue Val = E(ErrorCodes.Value);

    private const double Max = 2958465; // 9999-12-31

    [Theory]
    [InlineData("DATE(2024,1)")]
    [InlineData("DATE(2024,1,1,1)")]
    [InlineData("YEAR()")]
    [InlineData("YEAR(1,2)")]
    [InlineData("MONTH(1,2)")]
    [InlineData("DAY()")]
    [InlineData("WEEKDAY()")]
    [InlineData("WEEKDAY(1,2,3)")]
    [InlineData("EDATE(1)")]
    [InlineData("EDATE(1,2,3)")]
    [InlineData("EOMONTH(1)")]
    [InlineData("DAYS(1)")]
    [InlineData("DAYS(1,2,3)")]
    public void WrongArity_IsParseError(string formula) => Assert.Equal(E(ErrorCodes.Parse), Run(formula));

    // ---- the §7.6 serial table ----

    [Theory]
    [InlineData(1, 1900, 1, 1)]
    [InlineData(31, 1900, 1, 31)]
    [InlineData(32, 1900, 2, 1)]
    [InlineData(59, 1900, 2, 28)]
    [InlineData(60, 1900, 2, 29)]   // the fictitious day (R69)
    [InlineData(61, 1900, 3, 1)]
    [InlineData(0, 1900, 1, 0)]
    [InlineData(366, 1900, 12, 31)]
    [InlineData(367, 1901, 1, 1)]
    [InlineData(25569, 1970, 1, 1)]
    [InlineData(45292, 2024, 1, 1)]
    [InlineData(45351, 2024, 2, 29)]
    [InlineData(2958465, 9999, 12, 31)]
    public void YearMonthDay_OfSerial(double serial, double y, double m, double d)
    {
        Assert.Equal(N(y), Run($"YEAR({serial})"));
        Assert.Equal(N(m), Run($"MONTH({serial})"));
        Assert.Equal(N(d), Run($"DAY({serial})"));
        if (serial >= 1) // DATE inverts it (serial 0 is day 0 of January, covered in Date_Serial1900Calendar)
            Assert.Equal(N(serial), Run($"DATE({y},{m},{d})"));
    }

    [Theory]
    [InlineData("YEAR(-1)")]
    [InlineData("YEAR(2958466)")]
    [InlineData("MONTH(2958466)")]
    [InlineData("DAY(-1)")]
    [InlineData("YEAR(1E+300)")]
    public void OutOfRangeSerial_IsNum(string formula) => Assert.Equal(Num, Run(formula));

    [Fact]
    public void BoundarySerials_TruncateTowardZero()
    {
        Assert.Equal(N(1900), Run("YEAR(-0.9)"));   // -0.9 truncates to 0
        Assert.Equal(N(0), Run("DAY(-0.9)"));
        Assert.Equal(N(9999), Run("YEAR(2958465.99)"));
        Assert.Equal(Num, Run("YEAR(2958466)"));
    }

    [Fact]
    public void SerialRoundTrip_SampledAcrossTheRange_MatchesDateOnly()
    {
        // Independent oracle: DateOnly is used only in this test, as a culture-free calendar for serials from 61 on.
        var date = new DateOnly(1900, 3, 1);
        for (int serial = 61; serial <= Max; serial++, date = serial <= Max ? date.AddDays(1) : date)
        {
            bool sample = serial <= 800 || serial % 101 == 0 || serial >= Max - 800 || (date.Month == 2 && date.Day >= 28 && date.Year % 4 == 0 && date.Year % 100 < 8);
            if (!sample) continue;
            Assert.Equal(N(date.Year), Run($"YEAR({serial})"));
            Assert.Equal(N(date.Month), Run($"MONTH({serial})"));
            Assert.Equal(N(date.Day), Run($"DAY({serial})"));
            Assert.Equal(N(serial), Run($"DATE({date.Year},{date.Month},{date.Day})"));
        }
    }

    // ---- DATE ----

    [Theory]
    [InlineData("DATE(2024,14,1)", 45689)]    // 2025-02-01
    [InlineData("DATE(2024,1,0)", 45291)]     // 2023-12-31
    [InlineData("DATE(2024,-1,1)", 45231)]    // 2023-11-01
    [InlineData("DATE(2024,1,-1)", 45290)]    // 2023-12-30
    [InlineData("DATE(2024,0,1)", 45261)]     // 2023-12-01
    [InlineData("DATE(2024,13,1)", 45658)]    // 2025-01-01
    [InlineData("DATE(2024,25,1)", 46023)]    // 2026-01-01
    [InlineData("DATE(2024,1,32)", 45323)]    // 2024-02-01
    [InlineData("DATE(2024,3,0)", 45351)]     // 2024-02-29
    [InlineData("DATE(2023,3,0)", 44985)]     // 2023-02-28
    [InlineData("DATE(2024,1,366)", 45657)]   // 2024-12-31
    public void Date_RollsOver(string formula, double serial) => Assert.Equal(N(serial), Run(formula));

    [Theory]
    [InlineData("DATE(1900,1,1)", 1)]
    [InlineData("DATE(1900,1,0)", 0)]
    [InlineData("DATE(1900,2,28)", 59)]
    [InlineData("DATE(1900,2,29)", 60)]       // R69: February 1900 has 29 days
    [InlineData("DATE(1900,2,30)", 61)]
    [InlineData("DATE(1900,3,0)", 60)]        // R69
    [InlineData("DATE(1900,3,1)", 61)]
    [InlineData("DATE(1900,1,60)", 60)]
    [InlineData("DATE(1900,1,61)", 61)]
    [InlineData("DATE(1900,14,1)", 398)]      // 1901-02-01
    [InlineData("DATE(1901,3,0)", 425)]       // 1901-02-28
    [InlineData("DATE(9999,12,31)", 2958465)]
    [InlineData("DATE(9999,12,32)", double.NaN)]   // beyond the range
    [InlineData("DATE(9999,13,1)", double.NaN)]
    [InlineData("DATE(1900,1,2958465)", 2958465)]
    [InlineData("DATE(1900,1,2958466)", double.NaN)]
    [InlineData("DATE(1900,0,31)", 0)]        // December 1899 starts at -30; + 30 = 0
    public void Date_Serial1900Calendar(string formula, double serial) =>
        Assert.Equal(double.IsNaN(serial) ? Num : N(serial), Run(formula));

    [Theory]
    [InlineData("DATE(0,1,1)", "DATE(1900,1,1)")]       // y 0..1899 -> y+1900
    [InlineData("DATE(0,3,1)", "DATE(1900,3,1)")]
    [InlineData("DATE(124,1,1)", "DATE(2024,1,1)")]
    [InlineData("DATE(1899,12,31)", "DATE(3799,12,31)")]
    [InlineData("DATE(1899.9,1,1)", "DATE(3799,1,1)")]
    [InlineData("DATE(-0.5,1,1)", "DATE(1900,1,1)")]    // truncates to 0
    [InlineData("DATE(9999.9,1,1)", "DATE(9999,1,1)")]
    public void Date_YearMapping(string formula, string expected) => Assert.Equal(Run(expected), Run(formula));

    [Theory]
    [InlineData("DATE(-1,1,1)")]
    [InlineData("DATE(10000,1,1)")]
    [InlineData("DATE(99999,1,1)")]
    [InlineData("DATE(-1E+300,1,1)")]
    [InlineData("DATE(0,0,0)")]       // month 0 is December 1899 (serial -30), day 0 one earlier: before the range
    [InlineData("DATE(1900,0,30)")]   // December 1899 starts at serial -30, so day 30 is -1
    public void Date_OutOfRange_IsNum(string formula) => Assert.Equal(Num, Run(formula));

    [Fact]
    public void Date_HugeMonthOrDay_IsNum()
    {
        Assert.Equal(Num, Run("DATE(2024,2147483648,1)"));
        Assert.Equal(Num, Run("DATE(2024,-2147483649,1)"));
        Assert.Equal(Num, Run("DATE(2024,1,2147483648)"));
        Assert.Equal(Num, Run("DATE(2024,1,-2147483649)"));
        Assert.Equal(Num, Run("DATE(2024,1E+300,1)"));
        Assert.Equal(Num, Run("DATE(2024,1,2147483647)"));          // inside int, but the result is out of range
        Assert.Equal(Num, Run("DATE(2024,2147483647,1)"));
        Assert.Equal(Num, Run("DATE(2024,-2147483648,1)"));
    }

    [Fact]
    public void Date_DayRollsAcrossYears()
    {
        Assert.Equal(Run("DATE(2025,1,1)"), Run("DATE(2024,1,367)"));
        Assert.Equal(Run("DATE(2023,1,1)"), Run("DATE(2024,1,-364)"));
    }

    [Fact]
    public void Date_FractionsTruncateTowardZero()
    {
        Assert.Equal(Run("DATE(2024,1,1)"), Run("DATE(2024.9,1.9,1.9)"));
        Assert.Equal(Run("DATE(2024,1,0)"), Run("DATE(2024,1,-0.9)"));   // -0.9 -> 0, not -1
        Assert.Equal(Run("DATE(2024,0,1)"), Run("DATE(2024,-0.9,1)"));
    }

    [Fact]
    public void Date_TextArguments()
    {
        Assert.Equal(Run("DATE(2024,1,1)"), Run("DATE(\"2024\",\"1\",\"1\")"));   // numeric text coerces
        Assert.Equal(Val, Run("DATE(\"2024-01-01\",1,1)"));
        Assert.Equal(Val, Run("DATE(\"abc\",1,1)"));
        Assert.Equal(Val, Run("DATE(2024,\"x\",1)"));
        Assert.Equal(Val, Run("YEAR(\"2024-01-01\")"));
        Assert.Equal(Val, Run("MONTH(\"1/1/2024\")"));
        Assert.Equal(N(2024), Run("YEAR(\"45292\")"));
    }

    [Fact]
    public void Date_BooleansAndBlanks()
    {
        Assert.Equal(N(1), Run("DATE(0,TRUE,TRUE)"));
        Assert.Equal(N(1900), Run("YEAR(A1)", new DictionaryCells()));   // blank is 0
        Assert.Equal(N(0), Run("DAY(A1)", new DictionaryCells()));
    }

    [Fact]
    public void Errors_PropagateInArgumentOrder()
    {
        Assert.Equal(E(ErrorCodes.Div0), Run("DATE(1/0,#N/A,1)"));
        Assert.Equal(E(ErrorCodes.NA), Run("DATE(2024,#N/A,1/0)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("DATE(-1,1,1/0)"));          // coercion errors before the range check
        Assert.Equal(E(ErrorCodes.Div0), Run("YEAR(1/0)"));
        Assert.Equal(E(ErrorCodes.NA), Run("WEEKDAY(#N/A,1/0)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("WEEKDAY(-1,1/0)"));         // serial range is checked after both arguments
        Assert.Equal(E(ErrorCodes.NA), Run("EDATE(#N/A,1/0)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("EOMONTH(-1,1/0)"));
        Assert.Equal(E(ErrorCodes.NA), Run("DAYS(#N/A,1/0)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("DAYS(1,1/0)"));
    }

    [Fact]
    public void OmittedArguments_AreValueErrors_ExceptWeekdayType()
    {
        Assert.Equal(Val, Run("DATE(2024,,1)"));
        Assert.Equal(Val, Run("EDATE(1,)"));
        Assert.Equal(Val, Run("EOMONTH(,1)"));
        Assert.Equal(Val, Run("DAYS(1,)"));
        Assert.Equal(N(2), Run("WEEKDAY(2,)"));          // R38: the type default (1) applies
    }

    // ---- WEEKDAY ----

    [Theory]
    // serial, type 1, type 2, type 3 (serial 1 = 1900-01-01 is a Sunday)
    [InlineData(1, 1, 7, 6)]
    [InlineData(2, 2, 1, 0)]
    [InlineData(3, 3, 2, 1)]
    [InlineData(4, 4, 3, 2)]
    [InlineData(5, 5, 4, 3)]
    [InlineData(6, 6, 5, 4)]
    [InlineData(7, 7, 6, 5)]
    [InlineData(8, 1, 7, 6)]
    [InlineData(0, 7, 6, 5)]        // serial 0: Saturday by the formula (D-46)
    [InlineData(45292, 2, 1, 0)]    // 2024-01-01 is a Monday
    [InlineData(45297, 7, 6, 5)]    // 2024-01-06 is a Saturday
    [InlineData(2958465, 6, 5, 4)]  // 9999-12-31 is a Friday
    public void Weekday_Types(double serial, double t1, double t2, double t3)
    {
        Assert.Equal(N(t1), Run($"WEEKDAY({serial})"));
        Assert.Equal(N(t1), Run($"WEEKDAY({serial},1)"));
        Assert.Equal(N(t2), Run($"WEEKDAY({serial},2)"));
        Assert.Equal(N(t3), Run($"WEEKDAY({serial},3)"));
    }

    [Theory]
    [InlineData("WEEKDAY(1,0)")]
    [InlineData("WEEKDAY(1,4)")]
    [InlineData("WEEKDAY(1,11)")]
    [InlineData("WEEKDAY(1,-1)")]
    [InlineData("WEEKDAY(-1)")]
    [InlineData("WEEKDAY(2958466)")]
    public void Weekday_Invalid_IsNum(string formula) => Assert.Equal(Num, Run(formula));

    [Fact]
    public void Weekday_Truncation_AndText()
    {
        Assert.Equal(N(2), Run("WEEKDAY(2.9)"));
        Assert.Equal(N(1), Run("WEEKDAY(2,2.9)"));      // type 2.9 -> 2: Monday = 1
        Assert.Equal(Num, Run("WEEKDAY(2,0.9)"));       // type 0.9 -> 0
        Assert.Equal(N(2), Run("WEEKDAY(\"2\",\"1\")"));
        Assert.Equal(Val, Run("WEEKDAY(\"2024-01-01\")"));
        Assert.Equal(Val, Run("WEEKDAY(2,\"x\")"));
        Assert.Equal(N(7), Run("WEEKDAY(-0.9)"));       // serial -0.9 truncates to 0
    }

    // ---- EDATE / EOMONTH ----

    [Theory]
    [InlineData("EDATE(DATE(2024,1,31),1)", "DATE(2024,2,29)")]
    [InlineData("EDATE(DATE(2023,1,31),1)", "DATE(2023,2,28)")]
    [InlineData("EDATE(DATE(2024,1,31),2)", "DATE(2024,3,31)")]
    [InlineData("EDATE(DATE(2024,3,31),1)", "DATE(2024,4,30)")]
    [InlineData("EDATE(DATE(2024,3,31),-1)", "DATE(2024,2,29)")]
    [InlineData("EDATE(DATE(2024,3,31),-13)", "DATE(2023,2,28)")]
    [InlineData("EDATE(DATE(2024,2,29),12)", "DATE(2025,2,28)")]
    [InlineData("EDATE(DATE(2024,2,29),48)", "DATE(2028,2,29)")]
    [InlineData("EDATE(DATE(2099,1,31),13)", "DATE(2100,2,28)")]     // 2100 is not a leap year
    [InlineData("EDATE(DATE(2000,1,31),1)", "DATE(2000,2,29)")]      // 2000 is
    [InlineData("EDATE(DATE(2096,2,29),48)", "DATE(2100,2,28)")]
    [InlineData("EDATE(DATE(2024,12,15),1)", "DATE(2025,1,15)")]
    [InlineData("EDATE(DATE(2024,1,15),-1)", "DATE(2023,12,15)")]
    [InlineData("EDATE(DATE(2024,1,15),0)", "DATE(2024,1,15)")]
    [InlineData("EDATE(DATE(2024,1,15),1.9)", "DATE(2024,2,15)")]     // truncation
    [InlineData("EDATE(DATE(2024,1,15),-1.9)", "DATE(2023,12,15)")]
    [InlineData("EDATE(45306.75,1)", "DATE(2024,2,15)")]              // serial truncates; the time part is dropped
    public void Edate(string formula, string expected) => Assert.Equal(Run(expected), Run(formula));

    [Theory]
    [InlineData("EOMONTH(DATE(2024,1,15),0)", "DATE(2024,1,31)")]
    [InlineData("EOMONTH(DATE(2024,1,15),1)", "DATE(2024,2,29)")]
    [InlineData("EOMONTH(DATE(2023,1,15),1)", "DATE(2023,2,28)")]
    [InlineData("EOMONTH(DATE(2100,1,1),1)", "DATE(2100,2,28)")]
    [InlineData("EOMONTH(DATE(2000,1,1),1)", "DATE(2000,2,29)")]
    [InlineData("EOMONTH(DATE(2024,1,31),-1)", "DATE(2023,12,31)")]
    [InlineData("EOMONTH(DATE(2024,3,31),-1)", "DATE(2024,2,29)")]
    [InlineData("EOMONTH(DATE(2024,12,1),1)", "DATE(2025,1,31)")]
    [InlineData("EOMONTH(DATE(2024,1,15),-14)", "DATE(2022,11,30)")]
    [InlineData("EOMONTH(DATE(2024,1,15),0.9)", "DATE(2024,1,31)")]
    [InlineData("EOMONTH(DATE(2024,1,15),-0.9)", "DATE(2024,1,31)")]
    public void Eomonth(string formula, string expected) => Assert.Equal(Run(expected), Run(formula));

    [Fact]
    public void EdateEomonth_RangeLimits()
    {
        Assert.Equal(N(Max), Run("EOMONTH(DATE(9999,12,1),0)"));
        Assert.Equal(Num, Run("EOMONTH(DATE(9999,12,1),1)"));
        Assert.Equal(Num, Run("EDATE(DATE(9999,12,31),1)"));
        Assert.Equal(N(Max - 1), Run("EDATE(DATE(9999,11,30),1)"));       // Dec 30
        Assert.Equal(N(Max), Run("EDATE(DATE(9999,12,31),0)"));
        Assert.Equal(N(Max), Run("EDATE(DATE(9999,10,31),2)"));
        Assert.Equal(Num, Run("EDATE(DATE(1900,3,1),-3)"));               // before 1900-01-01
        Assert.Equal(N(1), Run("EDATE(DATE(1900,3,1),-2)"));
        Assert.Equal(Num, Run("EDATE(DATE(2024,1,1),-1E+9)"));
        Assert.Equal(Num, Run("EDATE(DATE(2024,1,1),1E+9)"));
        Assert.Equal(Num, Run("EOMONTH(DATE(2024,1,1),1E+300)"));
        Assert.Equal(Num, Run("EOMONTH(DATE(2024,1,1),-1E+300)"));
        Assert.Equal(Num, Run("EOMONTH(-1,0)"));
        Assert.Equal(Num, Run("EDATE(2958466,0)"));
        Assert.Equal(Num, Run("EDATE(-1,0)"));
    }

    [Fact]
    public void EdateEomonth_BeforeSerial61_UseTheSerialCalendar()
    {
        // D-46: the serial is split by the YEAR/MONTH/DAY rules and February 1900 has 29 days.
        Assert.Equal(N(60), Run("EDATE(31,1)"));        // Jan 31 + 1 month -> Feb 29 1900 (the fictitious day)
        Assert.Equal(N(60), Run("EDATE(60,0)"));
        Assert.Equal(N(32), Run("EDATE(1,1)"));         // Jan 1 -> Feb 1
        Assert.Equal(N(89), Run("EDATE(60,1)"));        // Feb 29 -> Mar 29 1900
        Assert.Equal(N(425), Run("EDATE(60,12)"));      // Feb 29 1900 -> Feb 1901, clamped to the 28th
        Assert.Equal(N(31), Run("EDATE(0,1)"));         // day 0 is kept: "1900-02-00"
        Assert.Equal(N(0), Run("EDATE(0,0)"));
        Assert.Equal(N(31), Run("EOMONTH(0,0)"));
        Assert.Equal(N(60), Run("EOMONTH(32,0)"));      // February 1900 ends on 60
        Assert.Equal(N(60), Run("EOMONTH(1,1)"));
        Assert.Equal(N(60), Run("EOMONTH(60,0)"));
        Assert.Equal(N(91), Run("EOMONTH(60,1)"));      // March 31 1900
        Assert.Equal(N(31), Run("EOMONTH(61,-2)"));
        Assert.Equal(N(60), Run("EDATE(DATE(1900,3,31),-1)"));   // Mar 31 -> Feb 1900 clamped to its 29th
    }

    [Fact]
    public void Edate_TextArguments()
    {
        Assert.Equal(Run("DATE(2024,2,15)"), Run("EDATE(\"45306\",\"1\")"));
        Assert.Equal(Val, Run("EDATE(\"2024-01-15\",1)"));
        Assert.Equal(Val, Run("EOMONTH(45306,\"one\")"));
    }

    // ---- DAYS ----

    [Theory]
    [InlineData("DAYS(10,3)", 7)]
    [InlineData("DAYS(3,10)", -7)]
    [InlineData("DAYS(10.9,3.1)", 7)]       // INT(10.9) - INT(3.1)
    [InlineData("DAYS(3.9,3.1)", 0)]
    [InlineData("DAYS(-0.5,0.5)", -1)]      // floor(-0.5) = -1, floor(0.5) = 0
    [InlineData("DAYS(-1.5,-2.5)", 1)]      // -2 - -3
    [InlineData("DAYS(0.5,-0.5)", 1)]
    [InlineData("DAYS(DATE(2024,3,1),DATE(2024,2,1))", 29)]
    [InlineData("DAYS(DATE(2025,1,1),DATE(2024,1,1))", 366)]
    [InlineData("DAYS(2958466,-5)", 2958471)]   // no range check (R69)
    [InlineData("DAYS(-100,-250)", 150)]
    [InlineData("DAYS(\"10\",\"3\")", 7)]
    [InlineData("DAYS(TRUE,FALSE)", 1)]
    public void Days(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Fact]
    public void Days_TextAndBlank()
    {
        Assert.Equal(Val, Run("DAYS(\"2024-02-01\",\"2024-01-01\")"));
        Assert.Equal(Val, Run("DAYS(1,\"a\")"));
        Assert.Equal(N(5), Run("DAYS(5,A1)", new DictionaryCells()));
    }

    // ---- through cells, case-insensitive names ----

    [Fact]
    public void ReferencesAndLowerCaseNames()
    {
        var cells = new DictionaryCells().Set("A1", 45292).Set("A2", 2);
        Assert.Equal(N(2024), Run("year(A1)", cells));
        Assert.Equal(N(45351), Run("eomonth(A1,A2-1)", cells));
        Assert.Equal(N(2), Run("weekday(A1)", cells));
        Assert.Equal(N(1), Run("weekday(A1,2)", cells));
    }
}
