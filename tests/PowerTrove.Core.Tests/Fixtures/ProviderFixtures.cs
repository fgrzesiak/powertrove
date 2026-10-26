using System;
using System.Collections.Generic;

namespace PowerTrove.Core.Tests.Fixtures;

public static class ProviderFixtures
{
    public static Fakes.FakeContextProvider CreateValidFileProvider(string id = "FakeFileProvider")
    {
        var files = new List<SelectedFile>
        {
            new SelectedFile(
                Path: "C:\\Fake\\Path\\test1.txt",
                Name: "test1.txt",
                Extension: ".txt",
                MediaType: "text/plain",
                SizeBytes: 1024,
                IsAvailableLocally: true,
                IsAccessible: true,
                Metadata: null)
        };

        return new Fakes.FakeContextProvider
        {
            Id = id,
            CanCaptureResult = true,
            CaptureStatus = ContextStatus.Available,
            CaptureContent = new FileSelection(files)
        };
    }

    public static Fakes.FakeContextProvider CreateValidTableProvider(string id = "FakeTableProvider")
    {
        var rows = new List<IReadOnlyList<CellValue>>
        {
            new List<CellValue> { new TextCell("Header1"), new TextCell("Header2") },
            new List<CellValue> { new NumberCell(1), new TextCell("Data") }
        };

        return new Fakes.FakeContextProvider
        {
            Id = id,
            CanCaptureResult = true,
            CaptureStatus = ContextStatus.Available,
            CaptureContent = new TableData(rows, "FakeExcel", "A1:B2")
        };
    }

    public static Fakes.FakeContextProvider CreateValidTextProvider(string id = "FakeTextProvider")
    {
        return new Fakes.FakeContextProvider
        {
            Id = id,
            CanCaptureResult = true,
            CaptureStatus = ContextStatus.Available,
            CaptureContent = new TextSelection("Fake text selection")
        };
    }

    public static Fakes.FakeContextProvider CreateDeniedProvider(string id = "FakeDeniedProvider")
    {
        return new Fakes.FakeContextProvider
        {
            Id = id,
            CanCaptureResult = true,
            CaptureStatus = ContextStatus.Denied,
            CaptureErrorMessage = "Access Denied"
        };
    }

    public static Fakes.FakeContextProvider CreateFailedProvider(string id = "FakeFailedProvider")
    {
        return new Fakes.FakeContextProvider
        {
            Id = id,
            CanCaptureResult = true,
            CaptureStatus = ContextStatus.Failed,
            CaptureErrorMessage = "An error occurred during capture"
        };
    }
}
