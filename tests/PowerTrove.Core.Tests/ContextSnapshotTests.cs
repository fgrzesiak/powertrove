using System;
using System.Collections.Generic;
using Xunit;
using PowerTrove.Core;

namespace PowerTrove.Core.Tests;

public class ContextSnapshotTests
{
    [Fact]
    public void ContextSnapshot_CanBeInitializedAndIsImmutable()
    {
        // Arrange
        var invocationId = Guid.NewGuid();
        var source = new SourceWindowIdentity(
            WindowHandle: 12345,
            ProcessId: 999,
            ProcessName: "explorer.exe",
            Title: "Test Window",
            CapturedAt: DateTimeOffset.UtcNow);
        var content = new NoContent();
        var capturedAt = DateTimeOffset.UtcNow;
        var expiresAt = capturedAt.AddMinutes(5);

        // Act
        var snapshot = new ContextSnapshot(
            InvocationId: invocationId,
            Source: source,
            Status: ContextStatus.Available,
            Content: content,
            CapturedAt: capturedAt,
            ExpiresAt: expiresAt,
            ProviderId: "TestProvider",
            UserMessage: null);

        // Assert
        Assert.Equal(invocationId, snapshot.InvocationId);
        Assert.Equal(source, snapshot.Source);
        Assert.Equal(ContextStatus.Available, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.Equal(expiresAt, snapshot.ExpiresAt);
        Assert.Equal("TestProvider", snapshot.ProviderId);
        Assert.Null(snapshot.UserMessage);
    }

    [Fact]
    public void ContextSnapshot_WithFileSelection_IsStoredCorrectly()
    {
        // Arrange
        var source = new SourceWindowIdentity(1, 1, "explorer", DateTimeOffset.UtcNow, "Test Title");
        var files = new List<SelectedFile>
        {
            new SelectedFile("C:\\test.txt", "test.txt", ".txt", "text/plain", 1024, true, true, null, false)
        };
        var content = new FileSelection(files);

        // Act
        var snapshot = new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: source,
            Status: ContextStatus.Available,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1),
            ProviderId: "FileProvider",
            UserMessage: null);

        // Assert
        var fileContent = Assert.IsType<FileSelection>(snapshot.Content);
        Assert.Single(fileContent.Files);
        Assert.Equal("C:\\test.txt", fileContent.Files[0].Path);
    }

    [Fact]
    public void ContextSnapshot_WithTableData_IsStoredCorrectly()
    {
        // Arrange
        var source = new SourceWindowIdentity(1, 1, "excel", DateTimeOffset.UtcNow, "Test Title");
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("Header1"), new TextCell("Header2") },
            new List<CellValue> { new NumberCell(1.0, "1.00"), new BooleanCell(true, "TRUE") }
        };
        var content = new TableData(rows, "Excel", "A1:B2");

        // Act
        var snapshot = new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: source,
            Status: ContextStatus.Available,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1),
            ProviderId: "TableProvider",
            UserMessage: null);

        // Assert
        var tableContent = Assert.IsType<TableData>(snapshot.Content);
        Assert.Equal(2, tableContent.Rows.Count);
        Assert.Equal("Excel", tableContent.SourceApplication);
        Assert.Equal("A1:B2", tableContent.SourceRange);

        var numCell = Assert.IsType<NumberCell>(tableContent.Rows[1][0]);
        Assert.Equal(1.0, numCell.Value);
        Assert.Equal("1.00", numCell.FormattedValue);

        var boolCell = Assert.IsType<BooleanCell>(tableContent.Rows[1][1]);
        Assert.True(boolCell.Value);
        Assert.Equal("TRUE", boolCell.FormattedValue);
    }

    [Fact]
    public void TableData_SupportsMixedTypesAndFormulasAsValues()
    {
        // Arrange
        var dt = new DateTime(2023, 10, 15);
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue>
            {
                new EmptyCell(),
                new DateCell(dt, "10/15/2023"),
                new NumberCell(42.5, "42.50") /* simulated formula result */,
                new ErrorCell("#DIV/0!", "#DIV/0!")
            }
        };

        // Act
        var tableContent = new TableData(rows, "SheetApp", "A1:D1");

        // Assert
        Assert.Single(tableContent.Rows);
        Assert.Equal(4, tableContent.Rows[0].Count);

        Assert.IsType<EmptyCell>(tableContent.Rows[0][0]);
        Assert.Null(tableContent.Rows[0][0].FormattedValue);

        var dateCell = Assert.IsType<DateCell>(tableContent.Rows[0][1]);
        Assert.Equal(dt, dateCell.Value);
        Assert.Equal("10/15/2023", dateCell.FormattedValue);

        var formulaValueCell = Assert.IsType<NumberCell>(tableContent.Rows[0][2]);
        Assert.Equal(42.5, formulaValueCell.Value);
        Assert.Equal("42.50", formulaValueCell.FormattedValue);

        var errCell = Assert.IsType<ErrorCell>(tableContent.Rows[0][3]);
        Assert.Equal("#DIV/0!", errCell.Code);
    }

    [Fact]
    public void TableData_SupportsEmptyRanges()
    {
        // Arrange
        var rows = new List<IReadOnlyList<CellValue>>(); // 0 rows

        // Act
        var tableContent = new TableData(rows, "App", null);

        // Assert
        Assert.Empty(tableContent.Rows);
    }

    [Fact]
    public void ContextSnapshot_WithUnsupportedStatus_DoesNotHaveToBeEmptySelection()
    {
        // Arrange
        var source = new SourceWindowIdentity(1, 1, "unknown", DateTimeOffset.UtcNow, "Test Title");
        var content = new NoContent();

        // Act
        var snapshot = new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: source,
            Status: ContextStatus.Unsupported,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1),
            ProviderId: "FallbackProvider",
            UserMessage: "The current application is not supported.");

        // Assert
        Assert.Equal(ContextStatus.Unsupported, snapshot.Status);
        Assert.IsType<NoContent>(snapshot.Content);
        Assert.Equal("The current application is not supported.", snapshot.UserMessage);
    }

    [Fact]
    public void ContextSnapshot_IsStale_ReturnsTrueWhenExpired()
    {
        // Arrange
        var source = new SourceWindowIdentity(1, 1, "unknown", DateTimeOffset.UtcNow, "Test Title");
        var content = new NoContent();

        // Act
        var snapshot = new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: source,
            Status: ContextStatus.Available,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            ProviderId: "FallbackProvider",
            UserMessage: null);

        // Assert
        Assert.True(snapshot.IsStale);
    }

    [Fact]
    public void ContextSnapshot_IsStale_ReturnsFalseWhenNotExpired()
    {
        // Arrange
        var source = new SourceWindowIdentity(1, 1, "unknown", DateTimeOffset.UtcNow, "Test Title");
        var content = new NoContent();

        // Act
        var snapshot = new ContextSnapshot(
            InvocationId: Guid.NewGuid(),
            Source: source,
            Status: ContextStatus.Available,
            Content: content,
            CapturedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ProviderId: "FallbackProvider",
            UserMessage: null);

        // Assert
        Assert.False(snapshot.IsStale);
    }
}
