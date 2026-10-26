using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PowerTrove.Abstractions;
using PowerTrove.Abstractions.Files;
using PowerTrove.Abstractions.Recipes;
using PowerTrove.Core;
using PowerTrove.Core.Recipes;
using PowerTrove.Core.Workflows;
using PowerTrove.Infrastructure.Files;
using PowerTrove.Pack.Files;
using PowerTrove.Pack.Images;
using Xunit;
using PowerTrove.Pack.Images.Magick;

namespace PowerTrove.Architecture.Tests.Recipes;

public class RecipeIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public RecipeIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PowerTroveTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task PrepareImagesForEmail_Recipe_ProducesOneZip_AndCleansIntermediates()
    {
        var fileSystem = new PhysicalOutputFileSystem();
        var services = new FileActionServices(fileSystem, new OutputPlanner(fileSystem), new PowerTrove.Abstractions.Testing.ExtensionOnlyTypeVerifier());

        var actionRegistry = new ActionRegistry();
        var workflowRunner = new WorkflowRunner(actionRegistry);

        var imageEngine = new CompositeImageEngine(new IImageEngine[] { new MagickImageEngine() });
        var imagesPack = new ImagesPack(imageEngine, services, null, null);
        var filesPack = new FilesPack(services);

        foreach (var action in imagesPack.Actions.Concat(filesPack.Actions))
        {
            actionRegistry.Register(action);
        }

        var recipe = new RecipeDefinition(
            "powertrove.recipe.images-for-email",
            "Prepare images for email",
            "Resizes images to max 1600 px, compresses them to 75% quality, and zips the result.",
            SelectionRequirements.Multiple(new[] { "image" }),
            new List<RecipeStep>
            {
                new RecipeStep("powertrove.image.resize", new Dictionary<string, string> { { "MaxDimension", "1600" }, { "Enlarge", "false" } }),
                new RecipeStep("powertrove.image.compress", new Dictionary<string, string> { { "Quality", "75" } }),
                new RecipeStep("powertrove.files.zip", new Dictionary<string, string>())
            }
        );
        var recipeAction = new RecipeAction(recipe, workflowRunner, services);
        actionRegistry.Register(recipeAction);

        var registeredRecipe = actionRegistry.GetAction("powertrove.recipe.images-for-email");
        Assert.NotNull(registeredRecipe);

        string inputImage = Path.Combine(_tempDir, "test.jpg");
        // Create a 1x1 JPG
        File.WriteAllBytes(inputImage, CreateJpg1x1());

        var selectedFile = new SelectedFile(inputImage, "test.jpg", ".jpg", "image/jpeg", 1000, true, true, null);
        var context = new ContextSnapshot(Guid.NewGuid(), new SourceWindowIdentity(IntPtr.Zero, 1, "test", DateTimeOffset.Now), ContextStatus.Available, new FileSelection(new[] { selectedFile }), DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(5), "test.provider", null);

        var receipt = await registeredRecipe.ExecuteAsync(context, new EmptyParameters(), null, CancellationToken.None);

        Assert.True(receipt.IsSuccess, "Recipe should succeed.");
        Assert.Single(receipt.OutputPaths);

        var outputPath = receipt.OutputPaths[0];
        Assert.EndsWith(".zip", outputPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_tempDir, outputPath, StringComparison.OrdinalIgnoreCase);

        var filesInDir = Directory.GetFiles(_tempDir);
        Assert.Equal(2, filesInDir.Length);
    }

    private static byte[] CreateJpg1x1()
    {
        // minimal valid jpeg
        return Convert.FromBase64String("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");
    }
}
