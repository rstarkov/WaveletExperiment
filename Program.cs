using System;
using System.Drawing;
using System.IO;
using RT.Util;
using TankIconMaker;

/// <summary>
/// - sine wave modulated by the current gaussian wavelet described by three extra numbers: frequency, phase, min brightness
/// 
/// ENTROPY CODING:
/// - calculate and record the true frequency of the most frequent symbols
/// - encode wavelet parameters using an exponential distribution arithmetic coding
/// - encode wavelet positions using Timwi's CEC idea?
/// - encode residuals using Timwi's CEC idea for optimal encoding of zeroes
/// - "slightly lossy" mode where residuals below a threshold are not encoded
/// - "slightly lossy" dithering to make higher thresholds more acceptable visually?
/// - encode residuals using scaling? 1, 2x2, 4x4 etc
/// - in lossless mode, it's critical to obtain residuals by reading wavelets in encoded order because addition of doubles is only approximately commutative
/// 
/// - low-pass filter in early stages to force initial wavelets to encode low-frequency info?
/// - high-pass filter the original image to find important pixels, encode / compress differences for those, then optimize wavelets based on compressed size
/// - at a certain point (scale-based? improvement slows down?) switch to analysing error locations and optimizing worst errors specifically, with local RNG and possibly a different metric (edge/feature-based?)
/// </summary>

namespace WaveletExperiment
{
    class Program
    {
        static void Main(string[] args)
        {
            var target = new Surface(Bitmap.FromFile(@"P:\WaveletExperiment\lena3.png").ToBitmapRam());
            target.Save("target.png");
            Rnd.Reset(12346);

            var opt2 = new Optimizer(target);
            opt2.LoadWavelets("wavelets-0701.txt");
            var ms = new MemoryStream();
            Codec.EncodeWavelets(ms, opt2.AllWavelets);
            File.WriteAllBytes("wavelets.dat", ms.ToArray());
            return;

            ////var p = 0;
            //var f = 65;
            ////for (int f = 0; f < 1000; f += 1)
            //for (int p = 0; p < 360; p += 1)
            //{
            //    var wvl = new Wavelet { X = 100 * 4, Y = 100 * 4, W = 100 * 4, H = 50 * 4, A = 30, Brightness = 255, TroughBrightness = 0, F = f, P = p };
            //    var img = new Surface(200, 200);
            //    img.ApplyWavelets(new[] { wvl });
            //    img.Save($"test-phase{p:000}-freq{f:000}.png");
            //}
            //return;

            var opt = new Optimizer(target);
            var start1 = DateTime.UtcNow;
            while (true)
            {
                var start2 = DateTime.UtcNow;
                opt.OptimizeStep();
                Console.WriteLine($"Step time: {(DateTime.UtcNow - start2).TotalSeconds:#,0.000}s. Total time: {(DateTime.UtcNow - start1).TotalSeconds:#,0.000}s");
            }
        }
    }
}
