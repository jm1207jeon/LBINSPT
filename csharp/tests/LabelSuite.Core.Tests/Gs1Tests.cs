using LabelSuite.Core;
using Xunit;

namespace LabelSuite.Core.Tests;

public class Gs1ParseTests
{
    [Fact]
    public void ConcatenatedFixedThenVariable()
    {
        var message = Gs1.Parse("01088061736123451727050910A123BC");
        Assert.Equal("08806173612345", message.Get("01"));
        Assert.Equal("270509", message.Get("17"));
        Assert.Equal("A123BC", message.Get("10"));
    }

    [Fact]
    public void LotValueContainingAiLikeDigits()
    {
        // LOT 값 '10ABC1723'의 '17'이 AI로 오인되면 안 된다 (레거시 버그)
        var message = Gs1.Parse("0108806173612345" + "10" + "10ABC1723");
        Assert.Equal("08806173612345", message.Get("01"));
        Assert.Equal("10ABC1723", message.Get("10"));
        Assert.Null(message.Get("17"));
    }

    [Fact]
    public void GsSeparatedVariableFields()
    {
        var message = Gs1.Parse("10LOT-A" + Gs1.GS + "21SER123");
        Assert.Equal("LOT-A", message.Get("10"));
        Assert.Equal("SER123", message.Get("21"));
    }

    [Fact]
    public void ParenthesizedHumanReadable()
    {
        var message = Gs1.Parse("(01)08806173612345(10)25090776(17)270509");
        Assert.Equal("08806173612345", message.Get("01"));
        Assert.Equal("25090776", message.Get("10"));
        Assert.Equal("270509", message.Get("17"));
    }

    [Fact]
    public void SymbologyPrefixStripped() =>
        Assert.Equal("08806173612345", Gs1.Parse("]d20108806173612345").Get("01"));

    [Fact]
    public void UnknownAiThrows() =>
        Assert.Throws<Gs1ParseException>(() => Gs1.Parse("9912345"));

    [Fact]
    public void ShortFixedFieldThrows() =>
        Assert.Throws<Gs1ParseException>(() => Gs1.Parse("01123"));

    [Fact]
    public void EmptyThrows() =>
        Assert.Throws<Gs1ParseException>(() => Gs1.Parse(""));
}

public class Gs1DateTests
{
    [Fact]
    public void Normal() => Assert.Equal(new DateOnly(2027, 5, 9), Gs1.ParseDate("270509"));

    [Fact]
    public void DayZeroIsMonthEnd()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), Gs1.ParseDate("270200"));
        Assert.Equal(new DateOnly(2028, 2, 29), Gs1.ParseDate("280200"));
    }

    [Fact]
    public void Invalid()
    {
        Assert.Null(Gs1.ParseDate("271350"));
        Assert.Null(Gs1.ParseDate("27050"));
    }
}

public class CrossCheckTests
{
    private static readonly LabelRecord Record = new(
        "25090776", "P", "PN", "REF", "2024-05-10", "2027-05-09", "08806173612345");

    [Fact]
    public void AllMatch()
    {
        var message = Gs1.Parse("(01)08806173612345(10)25090776(11)240510(17)270509");
        var checks = BarcodeCrossCheck.Check(message, Record, "DataMatrix");
        Assert.Equal(4, checks.Count);
        Assert.All(checks, c => Assert.True(c.Matched));
    }

    [Fact]
    public void GtinMismatch()
    {
        var message = Gs1.Parse("(01)08806173699999(10)25090776");
        var checks = BarcodeCrossCheck.Check(message, Record, "DataMatrix")
            .ToDictionary(c => c.Field);
        Assert.False(checks["GTIN"].Matched);
        Assert.True(checks["LOT"].Matched);
    }

    [Fact]
    public void BscDotDatesMatch()
    {
        var record = Record with { MfgDate = "2024.05.10", ExpDate = "2027.05.09" };
        var message = Gs1.Parse("(01)08806173612345(17)270509");
        var checks = BarcodeCrossCheck.Check(message, record, "DataMatrix")
            .ToDictionary(c => c.Field);
        Assert.True(checks["EXP DATE"].Matched);
    }

    [Fact]
    public void AbsentAiNotChecked()
    {
        var checks = BarcodeCrossCheck.Check(
            Gs1.Parse("(01)08806173612345"), Record, "Code128");
        Assert.Equal(["GTIN"], checks.Select(c => c.Field).ToArray());
    }
}
