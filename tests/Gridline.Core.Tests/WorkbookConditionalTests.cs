namespace Gridline.Core.Tests;

// Requirement IDs: §7.5, §6.1, §6.7 (R68: the resized sum_range rectangle is a static dependency), §9.5, §9.6, D-41, D-45.
public class WorkbookConditionalTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);

    private static Workbook Book(params (string Address, string Input)[] cells) =>
        Workbook.FromCells(cells.Select(c => new KeyValuePair<string, string>(c.Address, c.Input)));

    /// <summary>A1:A3 = x, y, x. B1:B5 = 1, 2, 4, 8, 16. D1 = SUMIF over A1:A3 with sum_range B1 (resized to B1:B3).</summary>
    private static Workbook Sheet(string formula = "=SUMIF(A1:A3,\"x\",B1)") =>
        Book(("A1", "x"), ("A2", "y"), ("A3", "x"), ("B1", "1"), ("B2", "2"), ("B3", "4"), ("B4", "8"), ("B5", "16"), ("D1", formula));

    [Theory]
    [InlineData("=SUMIF(A1:A3,\"x\",B1)")]
    [InlineData("=sumif(A1:A3,\"x\",B1)")]
    [InlineData("=SUMIF(A1:A3,\"x\",(B1))")]
    [InlineData("=SUMIF(A1:A3,\"x\",B1:B1)")]
    public void EditInsideResizedRect_OutsideTextualExtent_Recalculates(string formula)
    {
        var wb = Sheet(formula);
        Assert.Equal(N(5), wb.GetValue("D1"));
        wb.SetCell("B3", "100");                       // inside B1:B3, not inside the text "B1"
        Assert.Equal(N(101), wb.GetValue("D1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B2", "50");                        // matched? no (y), but inside the rectangle: still recalculated
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B4", "7");                         // outside the rectangle: nothing depends on it
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(101), wb.GetValue("D1"));
    }

    [Fact]
    public void AverageIf_ResizedRect_Recalculates()
    {
        var wb = Sheet("=AVERAGEIF(A1:A3,\"x\",B1)");
        Assert.Equal(N(2.5), wb.GetValue("D1"));
        wb.SetCell("B3", "8");
        Assert.Equal(N(4.5), wb.GetValue("D1"));
    }

    [Fact]
    public void CountIf_AndTwoArgumentForms_DependOnlyOnRange()
    {
        var wb = Sheet("=COUNTIF(A1:A3,\"x\")");
        wb.SetCell("B3", "1");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A2", "x");
        Assert.Equal(N(3), wb.GetValue("D1"));
    }

    [Fact]
    public void UndoAndRedo_KeepTheResizedDependency()
    {
        var wb = Sheet();
        wb.SetCell("B3", "100");
        Assert.True(wb.Undo());
        Assert.Equal(N(5), wb.GetValue("D1"));
        Assert.True(wb.Redo());
        Assert.Equal(N(101), wb.GetValue("D1"));
        wb.SetCell("D1", "=1");                         // replacing the formula drops the dependency
        wb.SetCell("B3", "0");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());                         // back to the SUMIF, with B3 = 100
        Assert.Equal(N(101), wb.GetValue("D1"));
        wb.SetCell("B2", "3");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    // ---- names (D-41, R68) ----

    private static Workbook WithColumnC(string formula)
    {
        var wb = Sheet("=1");
        wb.SetCell("C1", "10"); wb.SetCell("C2", "20"); wb.SetCell("C3", "40"); wb.SetCell("C4", "80");
        wb.DefineName("Vals", "$B$1");
        wb.DefineName("Keys", "$A$1:$A$3");
        wb.SetCell("D1", formula);
        return wb;
    }

    [Fact]
    public void NameAsSumRange_Redefined_MovesTheDependency()
    {
        var wb = WithColumnC("=SUMIF(A1:A3,\"x\",Vals)");
        Assert.Equal(N(5), wb.GetValue("D1"));
        wb.SetCell("B3", "100");                        // inside the name's resized rectangle B1:B3
        Assert.Equal(N(101), wb.GetValue("D1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);

        wb.DefineName("Vals", "$C$1");                  // redefined: now C1:C3
        Assert.Equal(N(50), wb.GetValue("D1"));
        wb.SetCell("C3", "41");                         // inside the new rectangle, outside the name's own cell
        Assert.Equal(N(51), wb.GetValue("D1"));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B3", "0");                          // the old rectangle no longer matters
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("C4", "0");                          // nor does a cell below the new rectangle
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);

    }

    [Fact]
    public void UndoOfNameRedefinition_RestoresTheDependency()
    {
        var wb = WithColumnC("=SUMIF(A1:A3,\"x\",Vals)");
        wb.DefineName("Vals", "$C$1");
        Assert.Equal(N(50), wb.GetValue("D1"));
        Assert.True(wb.Undo());
        Assert.Equal(N(5), wb.GetValue("D1"));
        wb.SetCell("B3", "6");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(7), wb.GetValue("D1"));
        wb.SetCell("C3", "0");                          // C no longer counts
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.DefineName("Vals", "$C$1");                  // defining it again moves the dependency again
        wb.SetCell("C3", "1");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void NameAsRange_Redefined_ResizesTheDependency()
    {
        var wb = WithColumnC("=SUMIF(Keys,\"x\",B1)");
        Assert.Equal(N(5), wb.GetValue("D1"));
        wb.DefineName("Keys", "$A$1:$A$2");             // the rectangle shrinks to B1:B2
        Assert.Equal(N(1), wb.GetValue("D1"));
        wb.SetCell("B3", "9");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B2", "9");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.DefineName("Keys", "$A$1:$A$4");             // and grows to B1:B4
        Assert.Equal(N(10), wb.GetValue("D1"));         // B1 + B3
        wb.SetCell("B4", "1000");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(10), wb.GetValue("D1"));         // A4 is blank, so B4 is not summed
    }

    [Fact]
    public void NameDeletedAndDefinedAgain_DropsAndRestoresTheDependency()
    {
        var wb = WithColumnC("=SUMIF(A1:A3,\"x\",Vals)");
        wb.DeleteName("Vals");
        Assert.Equal(CellValue.FromError(ErrorCodes.Name), wb.GetValue("D1"));
        wb.SetCell("B3", "100");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);   // no target, no rectangle
        wb.DefineName("Vals", "$B$2");                          // B2:B4
        Assert.Equal(N(10), wb.GetValue("D1"));                 // B2 + B4
        wb.SetCell("B4", "0");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(2), wb.GetValue("D1"));
        wb.SetCell("B1", "0");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void NameForBothRangeAndSumRange()
    {
        var wb = WithColumnC("=SUMIF(Keys,\"x\",Vals)");
        Assert.Equal(N(5), wb.GetValue("D1"));
        wb.SetCell("B3", "6");
        Assert.Equal(N(7), wb.GetValue("D1"));
        wb.DefineName("Keys", "$A$1:$A$2");
        wb.DefineName("Vals", "$C$2");                  // C2:C3
        Assert.Equal(N(20), wb.GetValue("D1"));         // only A1 = x matches, paired with C2
        wb.SetCell("C3", "5");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void ResizedRect_SurvivesInsertAndDeleteRows()
    {
        var wb = Sheet();
        wb.InsertRows(2, 1);                            // A1:A4, B1 stays: the rectangle is B1:B4
        Assert.Equal("=SUMIF(A1:A4,\"x\",B1)", wb.GetInput("D1"));
        wb.SetCell("B4", "100");                        // the old B3, still inside
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(101), wb.GetValue("D1"));
        wb.SetCell("B5", "1");                          // below
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.DeleteRows(2, 1);
        wb.SetCell("B3", "7");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(8), wb.GetValue("D1"));
        wb.SetCell("B4", "1");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void ResizedRect_ThroughNames_SurvivesInsertRows()
    {
        var wb = WithColumnC("=SUMIF(Keys,\"x\",Vals)");
        wb.InsertRows(1, 1);                            // Keys = A2:A4, Vals = B2, the formula moves to D2
        Assert.Equal(N(5), wb.GetValue("D2"));
        wb.SetCell("B4", "100");                        // inside B2:B4
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(101), wb.GetValue("D2"));
        wb.SetCell("B1", "9");                          // above the rectangle
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B5", "9");                          // below it
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());                         // back before the insert
        wb.SetCell("B3", "3");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void CycleThroughTheResizedRect_IsCirc()
    {
        var wb = Sheet();
        var circ = CellValue.FromError(ErrorCodes.Circ);
        wb.SetCell("B3", "=D1+1");                      // B3 is inside B1:B3 but not in the formula's text
        Assert.Equal(circ, wb.GetValue("D1"));
        Assert.Equal(circ, wb.GetValue("B3"));
        wb.SetCell("B3", "4");
        Assert.Equal(N(5), wb.GetValue("D1"));
    }

    [Fact]
    public void CycleThroughTheResizedRect_WithNames_IsCirc()
    {
        var wb = WithColumnC("=SUMIF(Keys,\"x\",Vals)");
        var circ = CellValue.FromError(ErrorCodes.Circ);
        wb.SetCell("B3", "=D1+1");
        Assert.Equal(circ, wb.GetValue("D1"));
        wb.DefineName("Vals", "$C$1");                  // the rectangle moves away from B3
        Assert.Equal(N(50), wb.GetValue("D1"));         // C1 + C3
        Assert.Equal(N(51), wb.GetValue("B3"));
    }
}
