using System;
using System.Drawing;
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
