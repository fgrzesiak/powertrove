namespace PowerTrove.Pack.Pdf;

public static class BlankPageDetector
{
    public static bool IsBlank(PageRaster raster, string sensitivity)
    {
        double threshold;
        if (sensitivity == "strict") threshold = 0.999;
        else if (sensitivity == "normal") threshold = 0.995;
        else if (sensitivity == "relaxed") threshold = 0.985;
        else threshold = 0.995;

        int whiteCount = 0;
        foreach (byte pixel in raster.Gray8)
        {
            if (pixel >= 240) whiteCount++;
        }

        return whiteCount >= (raster.Width * raster.Height) * threshold;
    }
}
