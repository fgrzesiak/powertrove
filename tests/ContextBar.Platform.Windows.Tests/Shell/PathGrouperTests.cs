using System.Collections.Generic;
using ContextBar.Platform.Windows.Shell;
using Xunit;
using System.IO;

namespace ContextBar.Platform.Windows.Tests.Shell
{
    public class PathGrouperTests
    {
        [Fact]
        public void GroupPathsByFolder_NormalPaths_GroupsCorrectly()
        {
            var paths = new List<string>
            {
                Path.Combine("C:", "folder1", "file1.txt"),
                Path.Combine("C:", "folder1", "file2.txt"),
                Path.Combine("C:", "folder2", "file3.txt")
            };

            var groups = PathGrouper.GroupPathsByFolder(paths);

            Assert.Equal(2, groups.Count);
            // Assert with system separator
            Assert.True(groups.ContainsKey(Path.Combine("C:", "folder1")));
            Assert.True(groups.ContainsKey(Path.Combine("C:", "folder2")));

            Assert.Equal(2, groups[Path.Combine("C:", "folder1")].Count);
            Assert.Single(groups[Path.Combine("C:", "folder2")]);
        }

        [Fact]
        public void GroupPathsByFolder_RootPaths_GroupsCorrectly()
        {
            // Drive roots only exist on Windows; Path.Combine("C:", x) would be drive-relative.
            if (!OperatingSystem.IsWindows())
                return;
            var paths = new List<string> { @"C:\file1.txt", @"D:\file2.txt" };

            var groups = PathGrouper.GroupPathsByFolder(paths);

            Assert.Equal(2, groups.Count);
            Assert.True(groups.ContainsKey(@"C:\"));
            Assert.True(groups.ContainsKey(@"D:\"));
        }

        [Fact]
        public void GroupPathsByFolder_EmptyAndNullPaths_IgnoresThem()
        {
            var paths = new List<string>
            {
                "",
                " ",
                null,
                Path.Combine("C:", "folder1", "file1.txt")
            };

            var groups = PathGrouper.GroupPathsByFolder(paths);

            Assert.Single(groups);
            Assert.True(groups.ContainsKey(Path.Combine("C:", "folder1")));
        }
    }
}
