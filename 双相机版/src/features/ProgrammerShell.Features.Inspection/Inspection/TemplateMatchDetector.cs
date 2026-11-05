using System.Threading;
using System.Text.Json;
using OpenCvSharp;

namespace ProgrammerShell.Features.Inspection.Inspection;

public sealed class TemplateMatchDetector : IDisposable
{
    private const double DefaultMatchThreshold = 0.6;
    private const int MaxPreviewDimension = 1600;
    private const string SettingsFileName = "inspection_settings.json";
    private readonly IReadOnlyList<TemplateSet> _templatesByCamera;
    private readonly double _matchThreshold;
    private static int _openCvConfigured;
    private bool _disposed;

    private sealed record InspectionSettings(double? TemplateMatchThreshold);

    public TemplateMatchDetector(string? templatesRoot = null)
    {
        ConfigureOpenCv();
        _matchThreshold = LoadMatchThreshold();

        var root = string.IsNullOrWhiteSpace(templatesRoot)
            ? GetDefaultTemplatesRoot()
            : templatesRoot;

        _templatesByCamera = LoadAll(root);
    }

    public (bool IsOk, Mat AnnotatedImage, int Total, int Successful) Detect(Mat bgr)
        => throw new InvalidOperationException("Call Detect(cameraIndex, bgr)");

    public (bool IsOk, Mat AnnotatedImage, int Total, int Successful) Detect(int cameraIndex, Mat image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cameraIndex is < 0 or > 3)
            cameraIndex = 0;

        var set = _templatesByCamera[cameraIndex];
        var imgGray = GetGrayView(image, out var ownsGray);

        try
        {
            var result = CreateAnnotatedPreview(image, imgGray, out var scaleX, out var scaleY);

            if (set.Templates.Count == 0)
            {
                DrawNoTemplate(result);
                return (false, result, 0, 0);
            }

            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));

            var successful = 0;
            var total = set.Templates.Count;
            var failed = new List<TemplateItem>();

            var h = imgGray.Rows;
            var w = imgGray.Cols;
            var baseScale = Math.Max(w, h) / 2000.0;
            var lineThickness = Math.Max(1, (int)(3 * baseScale));
            var fontScale = Math.Max(0.5, 1.0 * baseScale);

            foreach (var t in set.Templates)
            {
                try
                {
                    var templateEq = t.TemplateEq;
                    var tw = templateEq.Cols;
                    var th = templateEq.Rows;

                    var roiX1 = Math.Max(0, t.X - 200);
                    var roiY1 = Math.Max(0, t.Y - 200);
                    var roiX2 = Math.Min(imgGray.Cols, t.X + t.W + 200);
                    var roiY2 = Math.Min(imgGray.Rows, t.Y + t.H + 200);

                    if (roiX2 <= roiX1 || roiY2 <= roiY1)
                    {
                        failed.Add(t);
                        continue;
                    }

                    using var roi = new Mat(imgGray, new Rect(roiX1, roiY1, roiX2 - roiX1, roiY2 - roiY1));
                    if (roi.Empty())
                    {
                        failed.Add(t);
                        continue;
                    }

                    using var roiEq = new Mat();
                    clahe.Apply(roi, roiEq);

                    if (roiEq.Rows < th || roiEq.Cols < tw)
                    {
                        failed.Add(t);
                        continue;
                    }

                    using var res = new Mat();
                    Cv2.MatchTemplate(roiEq, templateEq, res, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(res, out _, out var maxVal, out _, out var maxLoc);

                    if (double.IsNaN(maxVal) || double.IsInfinity(maxVal) || maxVal < _matchThreshold)
                    {
                        failed.Add(t);
                        continue;
                    }

                    successful++;
                    var pt = new Point(maxLoc.X + roiX1, maxLoc.Y + roiY1);
                    var previewRect = ScaleRect(pt.X, pt.Y, tw, th, scaleX, scaleY, result);
                    var previewTextPoint = new Point(previewRect.Left, Math.Max(20, previewRect.Top - 10));
                    Cv2.Rectangle(result, previewRect.TopLeft, previewRect.BottomRight, Scalar.LimeGreen, lineThickness);
                    Cv2.PutText(result, t.Type.ToString(), previewTextPoint, HersheyFonts.HersheySimplex, fontScale, Scalar.LimeGreen, lineThickness);
                }
                catch
                {
                    failed.Add(t);
                }
            }

            foreach (var t in failed)
                DrawFail(result, t, lineThickness, fontScale, scaleX, scaleY);

            var isOk = successful == total;
            var statusFontScale = Math.Max(1, (int)(10 * baseScale));
            var statusThickness = Math.Max(2, (int)(10 * baseScale));
            Cv2.PutText(
                result,
                isOk ? "OK" : "NG",
                new Point(w / 4, h / 2),
                HersheyFonts.HersheySimplex,
                statusFontScale,
                isOk ? Scalar.LimeGreen : Scalar.Red,
                statusThickness);

            return (isOk, result, total, successful);
        }
        finally
        {
            if (ownsGray)
                imgGray.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var set in _templatesByCamera)
        {
            foreach (var template in set.Templates)
                template.TemplateEq.Dispose();
        }

        _disposed = true;
    }

    private static Mat GetGrayView(Mat image, out bool ownsGray)
    {
        ownsGray = image.Channels() != 1;

        return image.Channels() switch
        {
            1 => image,
            3 => image.CvtColor(ColorConversionCodes.BGR2GRAY),
            4 => image.CvtColor(ColorConversionCodes.BGRA2GRAY),
            _ => image.CvtColor(ColorConversionCodes.BGR2GRAY)
        };
    }

    private static void DrawNoTemplate(Mat image)
    {
        var h = image.Rows;
        var w = image.Cols;
        var baseScale = Math.Max(w, h) / 2000.0;
        var fontScale = Math.Max(1, (int)(3 * baseScale));
        var thickness = Math.Max(2, (int)(4 * baseScale));

        Cv2.PutText(
            image,
            "NO TEMPLATE",
            new Point(10, Math.Max(40, h / 2)),
            HersheyFonts.HersheySimplex,
            fontScale,
            Scalar.Red,
            thickness);
    }

    private static void DrawFail(Mat image, TemplateItem t, int lineThickness, double fontScale, double scaleX, double scaleY)
    {
        var previewRect = ScaleRect(t.X, t.Y, t.W, t.H, scaleX, scaleY, image);
        var previewTextPoint = new Point(previewRect.Left, Math.Max(20, previewRect.Top - 10));
        Cv2.Rectangle(image, previewRect.TopLeft, previewRect.BottomRight, Scalar.Red, lineThickness * 2);
        Cv2.PutText(image, $"{t.Type} (NG)", previewTextPoint, HersheyFonts.HersheySimplex, fontScale, Scalar.Red, lineThickness);
    }

    private static Mat CreateAnnotatedPreview(Mat originalImage, Mat grayImage, out double scaleX, out double scaleY)
    {
        var width = grayImage.Cols;
        var height = grayImage.Rows;
        var scale = Math.Min(1.0, MaxPreviewDimension / (double)Math.Max(width, height));
        var previewWidth = Math.Max(1, (int)Math.Round(width * scale));
        var previewHeight = Math.Max(1, (int)Math.Round(height * scale));

        scaleX = previewWidth / (double)width;
        scaleY = previewHeight / (double)height;

        if (originalImage.Channels() == 3)
        {
            return originalImage.Resize(new Size(previewWidth, previewHeight));
        }

        if (originalImage.Channels() == 4)
        {
            using var previewBgra = originalImage.Resize(new Size(previewWidth, previewHeight));
            return previewBgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }

        using var previewGray = grayImage.Resize(new Size(previewWidth, previewHeight));
        return previewGray.CvtColor(ColorConversionCodes.GRAY2BGR);
    }

    private static Rect ScaleRect(int x, int y, int width, int height, double scaleX, double scaleY, Mat canvas)
    {
        var left = Math.Clamp((int)Math.Round(x * scaleX), 0, Math.Max(0, canvas.Cols - 1));
        var top = Math.Clamp((int)Math.Round(y * scaleY), 0, Math.Max(0, canvas.Rows - 1));
        var right = Math.Clamp((int)Math.Round((x + width) * scaleX), left + 1, canvas.Cols);
        var bottom = Math.Clamp((int)Math.Round((y + height) * scaleY), top + 1, canvas.Rows);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static IReadOnlyList<TemplateSet> LoadAll(string root)
    {
        var map = new (string yml, string bmp)[]
        {
            ("backside1.yml", "backside1.bmp"),
            ("backside2.yml", "backside2.bmp"),
            ("backside3.yml", "backside3.bmp"),
            ("backside4.yml", "backside4.bmp")
        };

        var sets = new List<TemplateSet>(4);
        for (var cam = 0; cam < 4; cam++)
        {
            var ymlPath = Path.Combine(root, map[cam].yml);
            var bmpPath = Path.Combine(root, map[cam].bmp);
            sets.Add(LoadCameraTemplates(ymlPath, bmpPath));
        }

        return sets;
    }

    private static TemplateSet LoadCameraTemplates(string ymlPath, string bmpPath)
    {
        var templates = new List<TemplateItem>();

        if (!File.Exists(ymlPath) || !File.Exists(bmpPath))
            return new TemplateSet(templates);

        using var templateImg = Cv2.ImRead(bmpPath, ImreadModes.Color);
        if (templateImg.Empty())
            return new TemplateSet(templates);

        using var fs = new FileStorage(ymlPath, FileStorage.Modes.Read);
        if (!fs.IsOpened())
            return new TemplateSet(templates);

        var node = fs["Templates"];
        if (node is null || node.IsNone || !node.IsSeq)
            return new TemplateSet(templates);

        for (var i = 0; i < node.Size; i++)
        {
            var n = node[i];
            if (n is null || !n.IsMap)
                continue;

            var posNode = n["position"];
            if (posNode is null || !posNode.IsSeq || posNode.Size < 4)
                continue;

            var x = posNode[0].ReadInt();
            var y = posNode[1].ReadInt();
            var w = posNode[2].ReadInt();
            var h = posNode[3].ReadInt();

            if (x < 0 || y < 0 || w <= 0 || h <= 0)
                continue;

            if (x + w > templateImg.Cols || y + h > templateImg.Rows)
                continue;

            var typeNode = n["type"];
            var type = typeNode is null ? 0 : typeNode.ReadInt();

            using var roiBgr = new Mat(templateImg, new Rect(x, y, w, h));
            using var templateBgr = roiBgr.Clone();
            using var templateGray = new Mat();
            Cv2.CvtColor(templateBgr, templateGray, ColorConversionCodes.BGR2GRAY);
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
            using var templateEq = new Mat();
            clahe.Apply(templateGray, templateEq);

            templates.Add(new TemplateItem(x, y, w, h, type, templateEq.Clone()));
        }

        return new TemplateSet(templates);
    }

    private static string GetDefaultTemplatesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")))
                return Path.Combine(dir.FullName, "templates", "default");

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "templates", "default");
    }

    private static void ConfigureOpenCv()
    {
        if (Interlocked.Exchange(ref _openCvConfigured, 1) != 0)
            return;

        Cv2.SetNumThreads(1);
    }

    private static double LoadMatchThreshold()
    {
        try
        {
            var path = ResolveSettingsPath();
            if (!File.Exists(path))
                return DefaultMatchThreshold;

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<InspectionSettings>(json);
            var threshold = settings?.TemplateMatchThreshold;
            if (threshold is null || double.IsNaN(threshold.Value) || double.IsInfinity(threshold.Value))
                return DefaultMatchThreshold;

            return Math.Clamp(threshold.Value, 0.0, 1.0);
        }
        catch
        {
            return DefaultMatchThreshold;
        }
    }

    private static string ResolveSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, SettingsFileName);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, SettingsFileName);
    }

    private sealed record TemplateSet(IReadOnlyList<TemplateItem> Templates);

    private sealed record TemplateItem(int X, int Y, int W, int H, int Type, Mat TemplateEq);
}
