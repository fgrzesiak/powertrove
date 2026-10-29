using FileKit.Imaging.Raster;

namespace FileKit.Imaging.Tests;

public sealed class ImageCanvasTests
{
    [Theory]
    [InlineData(1, 1, false, 0)]
    [InlineData(-1, 1, false, 0)]
    [InlineData(1, -1, false, 0)]
    [InlineData(-1, -1, false, 0)]
    [InlineData(1, 1, false, 90)]
    [InlineData(1, 1, true, 0)]
    [InlineData(-1, -1, true, 0)]
    public void HeldCornerRemainsContinuousWhenHorizontalAndVerticalMotionOppose(int horizontal, int vertical, bool canvas, int angle)
    {
        var start = new ImageCanvasLayout(200, 100, 200, 100, Angle: angle);
        var state = new ImageCanvasEditorState(200, 100, start);
        (int Width, int Height)? previous = null;
        for (int i = 0; i < 40; i++)
        {
            double x = horizontal * 80, y = vertical * (i % 2 == 0 ? -39 : -41);
            double dx = canvas ? x : Math.Cos(start.Radians) * x - Math.Sin(start.Radians) * y;
            double dy = canvas ? y : Math.Sin(start.Radians) * x + Math.Cos(start.Radians) * y;
            Assert.True(state.Drag(start, horizontal, vertical, dx, dy, canvas, constrain: true));
            var size = canvas ? (state.Layout.CanvasWidth, state.Layout.CanvasHeight) : (state.Layout.ImageWidth, state.Layout.ImageHeight);
            if (previous is { } last) { Assert.InRange(Math.Abs(size.Item1 - last.Width), 0, 2); Assert.InRange(Math.Abs(size.Item2 - last.Height), 0, 2); }
            previous = size;
            if (!canvas)
            {
                double OppositeX(ImageCanvasLayout l) => l.CenterX - horizontal * l.ImageWidth / 2.0 * Math.Cos(l.Radians) + vertical * l.ImageHeight / 2.0 * Math.Sin(l.Radians);
                double OppositeY(ImageCanvasLayout l) => l.CenterY - horizontal * l.ImageWidth / 2.0 * Math.Sin(l.Radians) - vertical * l.ImageHeight / 2.0 * Math.Cos(l.Radians);
                Assert.InRange(Math.Abs(OppositeX(state.Layout) - OppositeX(start)), 0, 1);
                Assert.InRange(Math.Abs(OppositeY(state.Layout) - OppositeY(start)), 0, 1);
            }
        }
        state.FinishGesture(start); var final = state.Layout;
        state.Undo(); Assert.Equal(start, state.Layout); Assert.False(state.CanUndo);
        state.Redo(); Assert.Equal(final, state.Layout);
        Assert.True(state.Drag(start, horizontal, vertical, 0, 0, canvas, constrain: true)); Assert.Equal(start, state.Layout);
    }

    [Theory]
    [InlineData(1, 0, 100, 0, 300, 150)]
    [InlineData(0, 1, 0, 50, 300, 150)]
    [InlineData(1, 1, 20, 100, 256, 128)]
    [InlineData(1, 1, -100, -10, 116, 58)]
    public void ConstrainedCanvasDragsPreserveItsOwnAspectWithoutScalingTheImage(int x, int y, double dx, double dy, int width, int height)
    {
        var start = new ImageCanvasLayout(200, 100, 80, 60, 12, -6);
        var state = new ImageCanvasEditorState(80, 60, start) { LockAspect = false };
        Assert.True(state.Drag(start, x, y, dx, dy, canvas: true, constrain: true));
        Assert.Equal(start with { CanvasWidth = width, CanvasHeight = height }, state.Layout);
        state.FinishGesture(start); state.Undo(); Assert.Equal(start, state.Layout);
        Assert.False(state.CanUndo); state.Redo(); Assert.Equal(width, state.Layout.CanvasWidth);
    }

    [Fact]
    public void ConstraintIsTemporaryAndUsesImageAxesAfterRotation()
    {
        var start = new ImageCanvasLayout(500, 500, 200, 100, Angle: 90);
        var state = new ImageCanvasEditorState(200, 100, start) { LockAspect = false };
        Assert.True(state.Drag(start, 1, 0, 0, 100, constrain: true));
        Assert.Equal((300, 150), (state.Layout.ImageWidth, state.Layout.ImageHeight));
        Assert.False(state.LockAspect);
        Assert.True(state.Drag(start, 1, 0, 0, 100));
        Assert.Equal((300, 100), (state.Layout.ImageWidth, state.Layout.ImageHeight));
        Assert.True(state.Drag(start, 0, 0, 20, 40, constrain: true));
        Assert.Equal((0, 40), (state.Layout.OffsetX, state.Layout.OffsetY));
        Assert.True(state.Drag(start, 0, 0, 40, 20, constrain: true));
        Assert.Equal((40, 0), (state.Layout.OffsetX, state.Layout.OffsetY));
    }

    [Fact]
    public void ConstrainedLimitsAndRotationSnappingRemainBounded()
    {
        var start = new ImageCanvasLayout(200, 100, 80, 60);
        var state = new ImageCanvasEditorState(80, 60, start);
        Assert.False(state.Drag(start, 1, 0, 20000, 0, canvas: true, constrain: true));
        Assert.Equal(start, state.Layout);
        Assert.Equal(30, ImageCanvasEditorState.SnapAngle(23));
        Assert.Equal(-30, ImageCanvasEditorState.SnapAngle(-23));
        Assert.Equal(-180, ImageCanvasEditorState.SnapAngle(179));
    }

    private static RasterImage Pattern(int width, int height)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = (byte)(i + 1); data[i + 1] = 80; data[i + 2] = 200; data[i + 3] = 255; }
        return new(width, height, data);
    }

    [Fact]
    public void IdentityRetainsEveryPixel()
    {
        var source = Pattern(5, 3);
        Assert.Equal(source.Bgra, ImageCanvasRenderer.Render(source, new(5, 3, 5, 3)).Bgra);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(-180)]
    public void RightAngleLayoutsMatchTheExistingPixelRotations(int angle)
    {
        var source = Pattern(3, 2);
        var expected = angle == 90 ? RasterOperations.Rotate90(source) : angle == -90 ? RasterOperations.Rotate270(source) : RasterOperations.Rotate180(source);
        var actual = ImageCanvasRenderer.Render(source, new(expected.Width, expected.Height, 3, 2, Angle: angle));
        Assert.Equal(expected.Bgra, actual.Bgra);
    }

    [Fact]
    public void OffsetLeavesTransparentCanvasAndKeepsSourcePixels()
    {
        var source = Pattern(2, 2);
        var result = ImageCanvasRenderer.Render(source, new(6, 4, 2, 2, OffsetX: 1));
        Assert.Equal(0, result.Bgra[3]);
        Assert.Equal(source.Bgra[0], result.Bgra[(1 * 6 + 3) * 4]);
        Assert.Equal(4, Enumerable.Range(0, 24).Count(i => result.Bgra[i * 4 + 3] > 0));
    }

    [Fact]
    public void BilinearSamplingDoesNotMixHiddenColorIntoTransparentEdges()
    {
        var source = new RasterImage(2, 1, [0, 0, 255, 255, 255, 0, 0, 0]);
        var result = ImageCanvasRenderer.Render(source, new(3, 1, 3, 1));
        Assert.Equal(128, result.Bgra[7]); Assert.Equal(255, result.Bgra[6]); Assert.Equal(0, result.Bgra[4]);
    }

    [Fact]
    public void LimitsAndCancellationApplyBeforeAllocation()
    {
        var source = Pattern(2, 2);
        Assert.Throws<RasterException>(() => ImageCanvasRenderer.Render(source, new(10000, 10000, 2, 2)));
        Assert.Throws<OperationCanceledException>(() => ImageCanvasRenderer.Render(source, new(2, 2, 2, 2), new(true)));
        var fit = ImageCanvasLayout.Fit(100, 100, 200, 100, 45);
        Assert.False(fit.IsClipped);
        Assert.Equal((640, 640), (ImageCanvasLayout.Fit(128, 128, 1280, 640).ImageWidth, ImageCanvasLayout.Fit(128, 128, 1280, 640).ImageHeight));
    }

    [Fact]
    public void EdgeDragKeepsAspectAndOppositeEdgeWhileOneGestureHasOneUndoStep()
    {
        var initial = new ImageCanvasLayout(400, 300, 100, 50);
        var state = new ImageCanvasEditorState(100, 50, initial);
        state.Drag(initial, 1, 0, 25, 0); state.Drag(initial, 1, 0, 100, 0); state.FinishGesture(initial);
        Assert.Equal((200, 100, 50, 0), (state.Layout.ImageWidth, state.Layout.ImageHeight, state.Layout.OffsetX, state.Layout.OffsetY));
        state.Undo(); Assert.Equal(initial, state.Layout); Assert.False(state.CanUndo);
        state.Redo(); Assert.Equal(200, state.Layout.ImageWidth);
        state.Undo(); state.SetImageWidth(120); Assert.False(state.CanRedo);
    }

    [Fact]
    public void RotatedDragUsesImageAxesAndUnlockedResizeCanChangeWidthIndependently()
    {
        var start = new ImageCanvasLayout(400, 300, 100, 50, Angle: 90);
        var state = new ImageCanvasEditorState(100, 50, start);
        Assert.True(state.Drag(start, 1, 0, 0, 50));
        Assert.Equal((150, 75, 0, 25), (state.Layout.ImageWidth, state.Layout.ImageHeight, state.Layout.OffsetX, state.Layout.OffsetY));
        state.LockAspect = false; Assert.True(state.SetImageWidth(200)); Assert.Equal(75, state.Layout.ImageHeight);
        Assert.True(state.Align(-1, 0)); Assert.False(state.Layout.IsClipped);
    }

    [Fact]
    public void CanvasResizeDoesNotStretchTheImageAndInvalidEditsKeepThePreviousLayout()
    {
        var start = new ImageCanvasLayout(100, 100, 100, 100);
        var state = new ImageCanvasEditorState(100, 100, start);
        Assert.True(state.Drag(start, 1, 1, 100, 50, canvas: true));
        Assert.Equal(new ImageCanvasLayout(200, 150, 100, 100), state.Layout);
        var before = state.Layout;
        Assert.False(state.Set(before with { CanvasWidth = 10000, CanvasHeight = 10000 })); Assert.Equal(before, state.Layout);
        Assert.True(state.Rotate(45)); Assert.True(state.FitCanvas()); Assert.False(state.Layout.IsClipped);
    }
}
