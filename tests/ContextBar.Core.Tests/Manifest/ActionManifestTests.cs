using System.IO;
using System.Text.Json;
using ContextBar.Abstractions.Manifest;
using Xunit;

namespace ContextBar.Core.Tests.Manifest;

public class ActionManifestTests
{
    [Fact]
    public void Manifest_CanDeserializeFuturePluginManifestExample()
    {
        var manifestPath = Path.Combine("Examples", "future-plugin-manifest.json");

        Assert.True(File.Exists(manifestPath), $"Example manifest not found at {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ActionManifest>(json);

        Assert.NotNull(manifest);
        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("community.image.tools", manifest.Id);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("Image Tools", manifest.DisplayName);

        Assert.NotNull(manifest.Permissions);
        Assert.Contains("files.read:selected", manifest.Permissions);

        Assert.NotNull(manifest.PlatformConstraints);
        Assert.NotNull(manifest.PlatformConstraints.Os);
        Assert.Contains("windows", manifest.PlatformConstraints.Os);

        Assert.NotNull(manifest.Actions);
        var action = Assert.Single(manifest.Actions);
        Assert.Equal("community.image.tools.to-webp", action.Id);
        Assert.Equal("Convert to WebP", action.Title);
        Assert.Contains("files", action.InputKinds);
        Assert.True(action.RequiresPreview);

        Assert.NotNull(action.Parameters);
        Assert.Equal(2, action.Parameters.Count);

        var qualityParam = action.Parameters[0];
        Assert.Equal("quality", qualityParam.Name);
        Assert.Equal("number", qualityParam.Type);
        Assert.False(qualityParam.Required);

        var modeParam = action.Parameters[1];
        Assert.Equal("mode", modeParam.Name);
        Assert.Equal("choice", modeParam.Type);
        Assert.True(modeParam.Required);
        Assert.NotNull(modeParam.Choices);
        Assert.Contains("lossy", modeParam.Choices);
    }

    [Fact]
    public void Manifest_OmitDefaultsForPasswords()
    {
        var action = new DummyManifestAction(
            new ContextBar.Abstractions.ActionDescriptor(
                "test.action", "Title", "Desc",
                new System.Collections.Generic.HashSet<ContextBar.Abstractions.ContentKind> { ContextBar.Abstractions.ContentKind.None },
                System.Array.Empty<string>(), false, false,
                Parameters: new[]
                {
                    ContextBar.Abstractions.ActionParameterDefinition.Text("Normal", "Normal", "mydefault"),
                    ContextBar.Abstractions.ActionParameterDefinition.Password("Secret", "Secret")
                }
            )
        );

        var pack = new DummyManifestPack(new[] { action });
        var manifest = ActionPackManifest.Create(pack);
        var json = ActionPackManifest.ToJson(manifest);
        var deserialized = JsonSerializer.Deserialize<ActionManifest>(json);

        Assert.NotNull(deserialized);
        var actions = deserialized.Actions;
        Assert.NotNull(actions);
        var firstAction = Assert.Single(actions);
        Assert.NotNull(firstAction.Parameters);
        Assert.Equal(2, firstAction.Parameters.Count);

        var normalParam = firstAction.Parameters[0];
        Assert.Equal("string", normalParam.Type);
        Assert.Equal("mydefault", normalParam.Default);

        var secretParam = firstAction.Parameters[1];
        Assert.Equal("password", secretParam.Type);
        Assert.Null(secretParam.Default);
    }

    private sealed class DummyManifestAction : ContextBar.Abstractions.IAction
    {
        public DummyManifestAction(ContextBar.Abstractions.ActionDescriptor descriptor) => Descriptor = descriptor;
        public ContextBar.Abstractions.ActionDescriptor Descriptor { get; }
        public System.Threading.Tasks.ValueTask<ContextBar.Abstractions.ActionAvailability> MatchAsync(ContextBar.Abstractions.ContextSnapshot context, System.Threading.CancellationToken cancellationToken) => default;
        public System.Threading.Tasks.ValueTask<ContextBar.Abstractions.ActionPreview> PreviewAsync(ContextBar.Abstractions.ContextSnapshot context, ContextBar.Abstractions.ActionParameters parameters, System.Threading.CancellationToken cancellationToken) => default;
        public System.Threading.Tasks.Task<ContextBar.Abstractions.ActionReceipt> ExecuteAsync(ContextBar.Abstractions.ContextSnapshot context, ContextBar.Abstractions.ActionParameters parameters, System.IProgress<ContextBar.Abstractions.ActionProgress>? progress, System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(new ContextBar.Abstractions.ActionReceipt(true, false, System.Array.Empty<string>(), System.Array.Empty<ContextBar.Abstractions.ActionFailure>(), false, null));
    }

    private sealed class DummyManifestPack : ContextBar.Abstractions.IActionPack
    {
        public DummyManifestPack(System.Collections.Generic.IReadOnlyList<ContextBar.Abstractions.IAction> actions) => Actions = actions;
        public ContextBar.Abstractions.ActionPackInfo Info => new("test", "1.0", "Test", "Test");
        public System.Collections.Generic.IReadOnlyList<ContextBar.Abstractions.IAction> Actions { get; }
    }
}
