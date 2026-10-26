using System;
using System.Collections.Generic;

namespace ContextBar.Abstractions.Files;

public interface IOutputPlanner
{
    OutputPlan PlanOutput(string sourcePath, string targetDirectory, string targetExtension, bool overwrite = false);

    /// <summary>
    /// Plans a collision-free file named <paramref name="baseName"/> + <paramref name="extension"/>
    /// in <paramref name="targetDirectory"/>. Paths in <paramref name="reserved"/> (case-insensitive)
    /// count as taken, so a preview of several outputs never proposes the same name twice.
    /// Never plans an overwrite.
    /// </summary>
    OutputPlan PlanFile(string targetDirectory, string baseName, string extension, ISet<string>? reserved = null);

    /// <summary>Plans a collision-free new directory. Never reuses an existing directory or file name.</summary>
    OutputPlan PlanDirectory(string targetDirectory, string name, ISet<string>? reserved = null);

    void PrepareOutputFolder(OutputPlan plan);
}
