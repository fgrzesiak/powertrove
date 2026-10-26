using System;
using System.Collections.Generic;
using System.IO;
using PowerTrove.Abstractions.Files;
using Xunit;

namespace PowerTrove.Core.Tests.Files;

public class MockOutputFileSystem : IOutputFileSystem
{
        public long GetFileLength(string path) { using var s = OpenRead(path); return s.Length; }
        public void MoveFile(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void MoveDirectory(string sourcePath, string destinationPath) => throw new System.NotSupportedException();
        public void DeleteDirectory(string path) => throw new System.NotSupportedException();

    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CreatedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CreateDirectoryShouldThrow { get; set; }

    public bool FileExists(string path) => ExistingFiles.Contains(path);

    public bool DirectoryExists(string path) => ExistingDirectories.Contains(path);

    public void CreateDirectory(string path)
    {
        if (CreateDirectoryShouldThrow)
            throw new UnauthorizedAccessException("Simulated access denial.");

        CreatedDirectories.Add(path);
    }

    public void DeleteFile(string path) => ExistingFiles.Remove(path);
    public Stream OpenRead(string path) => throw new NotImplementedException();
    public Stream CreateFile(string path) => throw new NotImplementedException();
}

public class OutputPlannerTests
{
    private static string OutDir = Path.Combine("C:", "out");
    private static string InImage = Path.Combine("C:", "in", "image.png");

    [Fact]
    public void PhysicalOutputFileSystem_CreateFile_NeverTruncatesExistingOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contextbar-safe-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.csv");
            File.WriteAllText(path, "important original data");

            var fileSystem = new PhysicalOutputFileSystem();
            Assert.Throws<IOException>(() =>
            {
                using var _ = fileSystem.CreateFile(path);
            });
            Assert.Equal("important original data", File.ReadAllText(path));

            var freshPath = Path.Combine(directory, "new-report.csv");
            using (var output = fileSystem.CreateFile(freshPath))
            {
                output.WriteByte(42);
            }
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(freshPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlanOutput_NoCollision_ReturnsOriginalNameWithNewExtension()
    {
        var mockFs = new MockOutputFileSystem();
        mockFs.ExistingDirectories.Add(OutDir);
        var planner = new OutputPlanner(mockFs);

        var plan = planner.PlanOutput(InImage, OutDir, ".webp");

        Assert.Equal(InImage, plan.OriginalPath);
        Assert.Equal(Path.Combine(OutDir, "image.webp"), plan.TargetPath);
        Assert.False(plan.RequiresFolderCreation);
        Assert.False(plan.WillOverwrite);
    }

    [Fact]
    public void PlanOutput_MissingExtensionDot_AddsDotAutomatically()
    {
        var mockFs = new MockOutputFileSystem();
        var planner = new OutputPlanner(mockFs);

        var plan = planner.PlanOutput(InImage, OutDir, "webp");

        Assert.Equal(Path.Combine(OutDir, "image.webp"), plan.TargetPath);
    }

    [Fact]
    public void PlanOutput_WithCollision_OverwriteFalse_AppendsCounter()
    {
        var mockFs = new MockOutputFileSystem();
        mockFs.ExistingFiles.Add(Path.Combine(OutDir, "image.webp"));
        mockFs.ExistingFiles.Add(Path.Combine(OutDir, "image (1).webp"));
        var planner = new OutputPlanner(mockFs);

        var plan = planner.PlanOutput(InImage, OutDir, ".webp", overwrite: false);

        Assert.Equal(Path.Combine(OutDir, "image (2).webp"), plan.TargetPath);
        Assert.False(plan.WillOverwrite);
    }

    [Fact]
    public void PlanOutput_WithCollision_OverwriteTrue_ReturnsSameNameAndWillOverwrite()
    {
        var mockFs = new MockOutputFileSystem();
        mockFs.ExistingFiles.Add(Path.Combine(OutDir, "image.webp"));
        var planner = new OutputPlanner(mockFs);

        var plan = planner.PlanOutput(InImage, OutDir, ".webp", overwrite: true);

        Assert.Equal(Path.Combine(OutDir, "image.webp"), plan.TargetPath);
        Assert.True(plan.WillOverwrite);
    }

    [Fact]
    public void PlanOutput_MissingDirectory_RequiresFolderCreationTrue()
    {
        var mockFs = new MockOutputFileSystem();
        var planner = new OutputPlanner(mockFs);
        var newFolder = Path.Combine(OutDir, "newfolder");

        var plan = planner.PlanOutput(InImage, newFolder, ".webp");

        Assert.True(plan.RequiresFolderCreation);
    }

    [Fact]
    public void PrepareOutputFolder_CreatesDirectoryIfRequired()
    {
        var mockFs = new MockOutputFileSystem();
        var planner = new OutputPlanner(mockFs);
        var newFolder = Path.Combine(OutDir, "newfolder");
        var plan = new OutputPlan(InImage, Path.Combine(newFolder, "image.webp"), true, false);

        planner.PrepareOutputFolder(plan);

        Assert.Contains(newFolder, mockFs.CreatedDirectories);
    }

    [Fact]
    public void PrepareOutputFolder_DoesNotCreateDirectoryIfNotRequired()
    {
        var mockFs = new MockOutputFileSystem();
        var planner = new OutputPlanner(mockFs);
        var plan = new OutputPlan(InImage, Path.Combine(OutDir, "image.webp"), false, false);

        planner.PrepareOutputFolder(plan);

        Assert.Empty(mockFs.CreatedDirectories);
    }

    [Fact]
    public void PrepareOutputFolder_ThrowsWhenInaccessible()
    {
        var mockFs = new MockOutputFileSystem { CreateDirectoryShouldThrow = true };
        var planner = new OutputPlanner(mockFs);
        var plan = new OutputPlan(InImage, Path.Combine("protected", "image.webp"), true, false);

        Assert.Throws<UnauthorizedAccessException>(() => planner.PrepareOutputFolder(plan));
    }
}
