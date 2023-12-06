using System.Text.RegularExpressions;
using RT.Util;
using WaveletExperiment;

namespace GenerateAnimation;

internal class Program
{
    static string TempPath;

    static void Main(string[] args)
    {
        using (new TempDir())
        {
            AnimateProgress(@"C:\temp\run1");
            EncodeFromTemp(@"C:\temp\run1.mp4");
        }
        AnimateOrderedBwAll(@"C:\temp\run1\wavelets-2500.txt", @"C:\temp\run1\target.png", @"C:\temp\run1.mp4");
    }

    static void AnimateProgress(string dumpPath)
    {
        var dir = new DirectoryInfo(dumpPath);
        var list = dir.GetFiles("dump-progress-*.png", SearchOption.AllDirectories).OrderByDescending(x => x.Name).ToList();
        var wvlsize = dir.GetFiles("target-*.wvl", SearchOption.AllDirectories).Select(f => (wvl: int.Parse(Regex.Match(f.Name, @"^target-(?<n>\d+).wvl$").Groups["n"].Value), sz: (int) f.Length)).ToDictionary(x => x.wvl, x => x.sz);
        var font = new Font("Segoe UI", 12);
        var brush = new SolidBrush(Color.White);
        for (int i = 0; i < list.Count; i++)
        {
            if (i % 100 == 0) Console.WriteLine($"Copying frames... {i} / {list.Count}");
            var match = Regex.Match(list[i].Name, @"^dump-progress-(?<fit>[0-9.]+)-((?<cfit>[0-9.]+)-)?(?<wvl>\d+)\.png$");
            if (!match.Success) throw new Exception();
            int wavelets = int.Parse(match.Groups["wvl"].Value);
            var error = double.Parse(match.Groups["fit"].Value);
            var png = Bitmap.FromFile(list[i].FullName);
            var newpng = new Bitmap(png.Width, png.Height + 20);
            using (var g = Graphics.FromImage(newpng))
            {
                g.Clear(Color.Black);
                g.DrawImageUnscaled(png, 0, 0);
                var str = $"Wavelets: {wavelets}";
                if (wvlsize.TryGetValue(wavelets, out int size))
                    str += $", size: {size} bytes";
                g.DrawString(str, font, brush, 10, png.Height - 1);
            }
            newpng.Save(Path.Combine(TempPath, $"frame-{i + 1:0000}.png"));
        }
    }

    static void AnimateOrderedBwAll(string waveletsFile, string targetFile, string mp4file)
    {
        void gen(string name, Func<IEnumerable<Wavelet>, IEnumerable<Wavelet>> order)
        {
            using var _ = new TempDir();
            AnimateOrderedBw(waveletsFile, targetFile, order);
            EncodeFromTemp(PathUtil.AppendBeforeExtension(mp4file, "." + name));
        }
        gen("progressive", ww => ww);
        gen("regressive", ww => ww.Reverse());
        gen("diagonal1", ww => ww.OrderBy(w => w.X + w.Y));
        gen("diagonal2", ww => ww.OrderBy(w => w.X + w.Y + w.W + w.H));
        gen("first-brightest", ww => ww.OrderByDescending(w => w.Brightness));
        gen("first-largest", ww => ww.OrderByDescending(w => w.W * w.H));
        gen("first-smallest", ww => ww.OrderBy(w => w.W * w.H));
        gen("first-angle", ww => ww.OrderBy(w => w.A));
        gen("first-blobbiest", ww => ww.OrderBy(w => Math.Max(w.W, w.H) / (double) Math.Min(w.W, w.H)));
        gen("first-sharpest", ww => ww.OrderByDescending(w => Math.Max(w.W, w.H) / (double) Math.Min(w.W, w.H)));
    }

    static void AnimateOrderedBw(string waveletsFile, string targetFile, Func<IEnumerable<Wavelet>, IEnumerable<Wavelet>> order)
    {
        var target = new Surface(new Bitmap(targetFile));
        AnimateOrderedBw(waveletsFile, target.Width, target.Height, target.Average, order);
    }

    static void AnimateOrderedBw(string waveletsFIle, int width, int height, double initial, Func<IEnumerable<Wavelet>, IEnumerable<Wavelet>> order)
    {
        var surface = new Surface(width, height, initial);
        var wavelets = Wavelet.LoadFromDump(waveletsFIle);
        foreach (var wavelet in order(wavelets))
        {
            surface.ApplyWavelets(new[] { wavelet }, 0);
            surface.Save(Path.Combine(TempPath, $"frame-{surface.WaveletCount:0000}.png"));
        }
    }

    private static void EncodeFromTemp(string mp4path)
    {
        CommandRunner.Run("ffmpeg", "-framerate", "60", "-i", Path.Combine(TempPath, "frame-%04d.png"), "-vf", "tpad=stop_mode=clone:stop_duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", mp4path).OutputNothing().Go();
        CommandRunner.Run("ffmpeg", "-i", mp4path, "-filter:v", "setpts=PTS/10,fps=60", "-crf", "12", PathUtil.AppendBeforeExtension(mp4path, ".10x")).OutputNothing().Go();
    }

    class TempDir : IDisposable
    {
        DirectoryInfo _temp;
        public TempDir() { _temp = Directory.CreateTempSubdirectory("wavelet-enc"); TempPath = _temp.FullName; }
        public void Dispose() { _temp.Delete(recursive: true); }
    }
}
