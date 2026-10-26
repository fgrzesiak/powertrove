using System.Collections.Generic;
using Xunit;
using PowerTrove.Core;

namespace PowerTrove.Core.Tests;

public class ActionContractsTests
{
    [Fact]
    public void ActionReceipt_InitializationAndImmutability()
    {
        // Arrange
        var outputPaths = new List<string> { "C:\\output.webp" };
        var failures = new List<ActionFailure>
        {
            new ActionFailure("C:\\error.jpg", "Conversion failed.")
        };

        // Act
        var receipt = new ActionReceipt(
            IsSuccess: false,
            IsCanceled: true,
            OutputPaths: outputPaths,
            Failures: failures,
            IsUndoable: false,
            Message: "Completed with errors.");

        // Assert
        Assert.False(receipt.IsSuccess);
        Assert.True(receipt.IsCanceled);
        Assert.Single(receipt.OutputPaths);
        Assert.Equal("C:\\output.webp", receipt.OutputPaths[0]);
        Assert.Single(receipt.Failures);
        Assert.Equal("C:\\error.jpg", receipt.Failures[0].Item);
        Assert.Equal("Conversion failed.", receipt.Failures[0].ErrorMessage);
        Assert.False(receipt.IsUndoable);
        Assert.Equal("Completed with errors.", receipt.Message);
    }

    [Fact]
    public void ActionAvailability_Initialization()
    {
        // Act
        var availability = new ActionAvailability(IsAvailable: false, Reason: "File type not supported.");

        // Assert
        Assert.False(availability.IsAvailable);
        Assert.Equal("File type not supported.", availability.Reason);
    }

    [Fact]
    public void ActionPreview_Initialization()
    {
        // Arrange
        var outputs = new List<string> { "new_file.txt" };
        var warnings = new List<string> { "Might overwrite." };

        // Act
        var preview = new ActionPreview(
            RequiresConfirmation: true,
            PreviewText: "This will create a new file.",
            ExpectedOutputs: outputs,
            Warnings: warnings);

        // Assert
        Assert.True(preview.RequiresConfirmation);
        Assert.Equal("This will create a new file.", preview.PreviewText);
        Assert.Single(preview.ExpectedOutputs);
        Assert.Single(preview.Warnings);
    }
}
