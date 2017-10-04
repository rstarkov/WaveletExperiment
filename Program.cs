using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.Streams;
using TankIconMaker;

namespace WaveletExperiment
{
    class Program
    {
        static void Main(string[] args)
        {
            var target = new Surface(Bitmap.FromFile(@"P:\WaveletExperiment\lena3.png").ToBitmapRam());
            target.Save("target.png");
            Rnd.Reset(12346);

            var opt = new Optimizer(target);
            var start1 = DateTime.UtcNow;
            while (true)
            {
                var start2 = DateTime.UtcNow;
                opt.OptimizeStep();
                Console.WriteLine($"Step time: {(DateTime.UtcNow - start2).TotalSeconds:#,0.000}s. Total time: {(DateTime.UtcNow - start1).TotalSeconds:#,0.000}s");
            }
        }

        private static void ResultsAnalysis(Surface target, string path, int from)
        {
            var files = new DirectoryInfo(path).GetFiles("wavelets-*.txt").OrderBy(f => f.Name);
            var results = new List<string>();
            foreach (var f in files)
            {
                var opt = new Optimizer(target);
                opt.LoadWavelets(f.FullName);
                if (opt.AllWavelets.Count < from)
                    continue;
                var ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 0);
                var length0 = ms.Length;
                ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 1);
                var length1 = ms.Length;
                ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 2);
                var length2 = ms.Length;
                ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 3);
                var length3 = ms.Length;

                ms = new MemoryStream();
                Codec.EncodeWaveletsCec(new DoNotCloseStream(ms), opt.AllWavelets);
                var lengthW = ms.Length;

                File.AppendAllLines(Path.Combine(path, "wavelets-zanalysis.txt"), new[] { $"At {opt.AllWavelets.Count} wavelets, RMS error = {Optimizer.TotalRmsError(opt.AllWavelets, new Surface(target.Width, target.Height), target)}, lossless = {length0:#,0} bytes, lossy 1 = {length1:#,0} bytes, lossy 2 = {length2:#,0} bytes, lossy 3 = {length3:#,0} bytes, wavelets only = {lengthW:#,0} bytes" });
            }
        }
    }
}
