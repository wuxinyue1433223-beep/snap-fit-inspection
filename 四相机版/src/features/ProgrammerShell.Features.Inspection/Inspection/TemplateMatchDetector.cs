using OpenCvSharp;
using System.Text.Json;

namespace ProgrammerShell.Features.Inspection.Inspection;

public sealed class TemplateMatchDetector : IDisposable
{
    private const int MaxAnnotatedPreviewDimension = 1280;
    private const double DefaultMatchThreshold = 0.6;
    private const string SettingsFileName = "inspection.settings.json";
    private readonly IReadOnlyList<TemplateSet> _templatesByCamera;
    private readonly double _matchThreshold;
    private bool _disposed;

    public TemplateMatchDetector(string? templatesRoot = null)
    {
        _matchThreshold = LoadMatchThreshold();

        var root = string.IsNullOrWhiteSpace(templatesRoot)
            ? GetDefaultTemplatesRoot()
            : templatesRoot;

        _templatesByCamera = LoadAll(root);
    }

    public (bool IsOk, Mat AnnotatedBgr, int Total, int Successful) Detect(Mat bgr)
        => throw new InvalidOperationException("Call Detect(cameraIndex, bgr)");

    public (bool IsOk, Mat AnnotatedBgr, int Total, int Successful) Detect(int cameraIndex, Mat image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cameraIndex is < 0 or > 3)
            cameraIndex = 0;

        var set = _templatesByCamera[cameraIndex];
        Mat? ownedGray = null;
        var imgGray = image;
        if (image.Channels() != 1)
        {
            ownedGray = NormalizeToGray(image);
            imgGray = ownedGray;
        }

        var result = CreateAnnotatedPreviewBgr(image);

        try
        {
            var scaleX = result.Cols / (double)Math.Max(1, image.Cols);
            var scaleY = result.Rows / (double)Math.Max(1, image.Rows);

            if (set.Templates.Count == 0)
            {
                DrawNoTemplate(result);
                return (false, result, 0, 0);
            }

            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));

            var successful = 0;
            var total = set.Templates.Count;
            var failed = new List<TemplateItem>();

            var h = result.Rows;
            var w = result.Cols;
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
                    var srcX = maxLoc.X + roiX1;
                    var srcY = maxLoc.Y + roiY1;
                    var p1 = ToPreviewPoint(srcX, srcY, scaleX, scaleY);
                    var p2 = ToPreviewPoint(srcX + tw, srcY + th, scaleX, scaleY);
                    Cv2.Rectangle(result, p1, p2, Scalar.LimeGreen, lineThickness);
                    Cv2.PutText(result, t.Type.ToString(), new Point(p1.X, p1.Y - 10), HersheyFonts.HersheySimplex, fontScale, Scalar.LimeGreen, lineThickness);
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
            ownedGray?.Dispose();
        }
    }

    private static Mat NormalizeToGray(Mat image)
    {
        return image.Channels() switch
        {
            1 => image.Clone(),
            3 => image.CvtColor(ColorConversionCodes.BGR2GRAY),
            4 => image.CvtColor(ColorConversionCodes.BGRA2GRAY),
            _ => image.CvtColor(ColorConversionCodes.BGR2GRAY)
        };
    }

    private static void DrawNoTemplate(Mat bgr)
    {
        var h = bgr.Rows;
        var w = bgr.Cols;
        var baseScale = Math.Max(w, h) / 2000.0;
        var fontScale = Math.Max(1, (int)(3 * baseScale));
        var thickness = Math.Max(2, (int)(4 * baseScale));

        Cv2.PutText(
            bgr,
            "NO TEMPLATE",
            new Point(10, Math.Max(40, h / 2)),
            HersheyFonts.HersheySimplex,
            fontScale,
            Scalar.Red,
            thickness);
    }

    private static void DrawFail(Mat bgr, TemplateItem t, int lineThickness, double fontScale, double scaleX, double scaleY)
    {
        var p1 = ToPreviewPoint(t.X, t.Y, scaleX, scaleY);
        var p2 = ToPreviewPoint(t.X + t.W, t.Y + t.H, scaleX, scaleY);
        Cv2.Rectangle(bgr, p1, p2, Scalar.Red, lineThickness * 2);
        Cv2.PutText(bgr, $"{t.Type} (NG)", new Point(p1.X, p1.Y - 10), HersheyFonts.HersheySimplex, fontScale, Scalar.Red, lineThickness);
    }

    private static Mat CreateAnnotatedPreviewBgr(Mat source)
    {
        var previewSize = GetPreviewSize(source.Cols, source.Rows);

        if (source.Channels() == 1)
        {
            using var previewGray = ResizeToPreview(source, previewSize);
            return previewGray.CvtColor(ColorConversionCodes.GRAY2BGR);
        }

        if (source.Channels() == 3)
        {
            return ResizeToPreview(source, previewSize);
        }

        if (source.Channels() == 4)
        {
            using var previewBgra = ResizeToPreview(source, previewSize);
            return previewBgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }

        using var fallbackGray = NormalizeToGray(source);
        using var previewFallbackGray = ResizeToPreview(fallbackGray, previewSize);
        return previewFallbackGray.CvtColor(ColorConversionCodes.GRAY2BGR);
    }

    private static Mat ResizeToPreview(Mat source, Size previewSize)
    {
        if (source.Cols == previewSize.Width && source.Rows == previewSize.Height)
            return source.Clone();

        var resized = new Mat();
        Cv2.Resize(source, resized, previewSize, 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private static Size GetPreviewSize(int width, int height)
    {
        if (width <= MaxAnnotatedPreviewDimension && height <= MaxAnnotatedPreviewDimension)
            return new Size(width, height);

        var scale = Math.Min((double)MaxAnnotatedPreviewDimension / Math.Max(1, width), (double)MaxAnnotatedPreviewDimension / Math.Max(1, height));
        return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static Point ToPreviewPoint(int x, int y, double scaleX, double scaleY)
        => new((int)Math.Round(x * scaleX), (int)Math.Round(y * scaleY));

    private static double LoadMatchThreshold()
    {
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
            if (!File.Exists(settingsPath))
                return DefaultMatchThreshold;

            var json = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(json))
                return DefaultMatchThreshold;

            var settings = JsonSerializer.Deserialize<InspectionSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var threshold = settings?.MatchThreshold;
            if (!threshold.HasValue || double.IsNaN(threshold.Value) || double.IsInfinity(threshold.Value))
                return DefaultMatchThreshold;

            if (threshold.Value is < 0 or > 1)
                return DefaultMatchThreshold;

            return threshold.Value;
        }
        catch
        {
            return DefaultMatchThreshold;
        }
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

    private sealed class InspectionSettings
    {
        public double? MatchThreshold { get; set; }
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

    private sealed record TemplateSet(IReadOnlyList<TemplateItem> Templates);

    private sealed record TemplateItem(int X, int Y, int W, int H, int Type, Mat TemplateEq);

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var set in _templatesByCamera)
        {
            foreach (var template in set.Templates)
            {
                try
                {
                    template.TemplateEq.Dispose();
                }
                catch
                {
                }
            }
        }

        _disposed = true;
    }
}
