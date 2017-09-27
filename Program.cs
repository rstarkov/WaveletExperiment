using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;
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
                opt.Optimize();
                Console.WriteLine($"Step time: {(DateTime.UtcNow - start2).TotalSeconds:#,0.000}s. Total time: {(DateTime.UtcNow - start1).TotalSeconds:#,0.000}s");
            }
        }
    }

    class Optimizer
    {
        private Surface _targetImage;
        public List<Wavelet> _allWavelets = new List<Wavelet>();

        public Optimizer(Surface target)
        {
            _targetImage = target;
        }

        public void LoadWavelets(string path)
        {
            LoadWavelets(File.ReadAllLines(path).Select(l => l.Replace("FINAL: ", "").Trim()).Where(l => l.StartsWith("X=")).Select(l => new Wavelet(l)).ToList());
        }

        public void LoadWavelets(IEnumerable<Wavelet> wavelets)
        {
            _allWavelets = wavelets.ToList();
            _lastDumpProgressError = TotalRmsError(_allWavelets, new Surface(_targetImage.Width, _targetImage.Height), _targetImage);
        }

        private double calcWaveletScale()
        {
            if (_allWavelets.Count <= 10)
                return _targetImage.Width * 0.6;
            double result = Math.Max((_allWavelets[0].W / 4.0).ClipMax(_targetImage.Width), (_allWavelets[0].H / 4.0).ClipMax(_targetImage.Height));
            foreach (var wvl in _allWavelets.Skip(1))
                result = 0.9 * result + 0.1 * Math.Max((wvl.W / 4.0).ClipMax(_targetImage.Width), (wvl.H / 4.0).ClipMax(_targetImage.Height));
            return result;
        }

        public void Optimize()
        {
            var img = new Surface(_targetImage.Width, _targetImage.Height);
            img.ApplyWavelets(_allWavelets);
            var scale = calcWaveletScale();
            var newWavelet = ChooseRandomWavelet(img, _targetImage, scale, _allWavelets.Count > 20, scale < 50);
            _allWavelets.Add(newWavelet);

            if (_allWavelets.Count <= 10 || (_allWavelets.Count < 50 && _allWavelets.Count % 2 == 0) || (_allWavelets.Count < 200 && _allWavelets.Count % 5 == 0) || (_allWavelets.Count % 20 == 0))
            {
                Console.WriteLine("Tweak all...");
                img = new Surface(_targetImage.Width, _targetImage.Height);
                _allWavelets = TweakWavelets(_allWavelets.ToArray(), img, _targetImage).ToList();
            }

            var newError = TotalRmsError(_allWavelets, new Surface(_targetImage.Width, _targetImage.Height), _targetImage);
            File.AppendAllLines("wavelets.txt", new[] { $"RMS error at {_allWavelets.Count} wavelets: {newError}" });
            File.WriteAllLines("wavelets-final.txt", _allWavelets.Select(w => "FINAL: " + w.ToString()));
            File.AppendAllLines("wavelets-final.txt", new[] { $"RMS FINAL error at {_allWavelets.Count} wavelets: {newError}" });
        }

        public void TweakAllWavelets()
        {
            var img = new Surface(_targetImage.Width, _targetImage.Height);
            _allWavelets = TweakWavelets(_allWavelets.ToArray(), img, _targetImage).ToList();
        }

        private Wavelet ChooseRandomWavelet(Surface initial, Surface target, double scale, bool tweakEveryGuess, bool errorGuided)
        {
            Surface errors = null;
            if (errorGuided)
            {
                errors = target.Clone();
                var cur = initial.Clone();
                cur.Process(p => p.Clip(0, 255));
                errors.Merge(cur, (t, c) => (t - c) * (t - c));
                errors.Blur3();
                errors.Blur3();
                var maxError = errors.Data.Max();
                errors.Process(p => p / maxError);
                //errors.Process(p => p * 255);
                //errors.Save("errors.png");
            }

            Wavelet best = null;
            double bestError = TotalRmsError(initial, target);
            double startingError = bestError;
            Console.Write($"ChooseRandomWavelet initial error: {bestError}, scale {scale:0.0}, {(errorGuided ? "error-guided" : "random")}...");
            for (int iter = 0; iter < 2000; iter++)
            {
                int x, y;
                while (true)
                {
                    x = Rnd.Next(0, target.Width);
                    y = Rnd.Next(0, target.Height);
                    if (errors == null || Rnd.NextDouble() < errors[x, y])
                        break;
                }

                var wavelet = new Wavelet
                {
                    X = x * 4 + Rnd.Next(0, 4),
                    Y = y * 4 + Rnd.Next(0, 4),
                    W = Rnd.Next((int) (4 * scale / 4), (int) (4 * scale * 4)),
                    H = Rnd.Next((int) (4 * scale / 4), (int) (4 * scale * 4)),
                    A = Rnd.Next(0, 360),
                    Brightness = Rnd.Next(-255, 255 + 1),
                };
                var newError = TotalRmsError(wavelet, initial, target);
                Console.Write($"ChRand {iter}: ");
                if (tweakEveryGuess)
                {
                    wavelet = TweakWavelets(new[] { wavelet }, initial, target, 10)[0];
                    newError = TotalRmsError(wavelet, initial, target);
                }
                if (newError < bestError)
                {
                    bestError = newError;
                    best = wavelet;
                    dumpProgress(bestError, initial, new[] { best });
                }
                if (best == null) // keep trying until we find at least one suitable wavelet
                    iter = 0;
            }
            Console.WriteLine($"best error: {bestError}, tweaking...");
            best = TweakWavelets(new[] { best }, initial, target)[0];
            var doubleCheckError = TotalRmsError(best, initial, target);
            if (doubleCheckError >= startingError)
                throw new Exception("ChooseRandomWavelet: error check failed");
            return best;
        }

        private Wavelet[] ChooseWavelets(Surface initial, Surface target, double scale)
        {
            Wavelet[] best = new Wavelet[0];
            double bestError = TotalRmsError(best, initial, target);
            for (int iter = 0; iter < 25; iter++)
            {
                Console.Write(iter + " ");
                var wavelets = Enumerable.Range(0, 1).Select(_ => new Wavelet
                {
                    X = Rnd.Next(0, target.Width * 4),
                    Y = Rnd.Next(0, target.Height * 4),
                    W = Rnd.Next((int) (4 * scale / 2), (int) (4 * scale * 2)),
                    H = Rnd.Next((int) (4 * scale / 2), (int) (4 * scale * 2)),
                    A = Rnd.Next(0, 360),
                    Brightness = Rnd.Next(-255, 255 + 1),
                }).ToArray();
                wavelets = OptimizeWavelets(wavelets, initial, target, 15, scale / 6);
                var error = TotalRmsError(wavelets, initial, target);
                if (bestError > error)
                {
                    bestError = error;
                    best = wavelets;
                }
                if (best.Length == 0) // keep trying until we find at least one suitable wavelet
                    iter = 0;
            }
            Console.Write($"Best error: {bestError}. Longer optimize...");
            var bestNew = OptimizeWavelets(best, initial, target, 15000, scale / 6);
            var bestNewError = TotalRmsError(bestNew, initial, target);
            if (bestNewError < bestError)
            {
                Console.WriteLine($" improved to {bestNewError}");
                best = bestNew;
            }
            else
                Console.WriteLine($" did not improve");
#warning TODO: it's not supposed to get worse! (or is it?)
            best = TweakWavelets(best, initial, target);
            return best;
        }

        private Wavelet[] TweakWavelets(Wavelet[] wavelets, Surface initial, Surface target, int iterations = 0)
        {
            var best = wavelets.Select(w => w.Clone()).ToArray();
            wavelets = best.Select(w => w.Clone()).ToArray();
            while (true)
            {
                var img = initial.Clone();
                img.ApplyWavelets(wavelets);
                var bestError = TotalRmsError(img, target);
                var initialError = bestError;

                if (iterations > 0)
                    iterations--;
                if (iterations == 1)
                {
                    Console.WriteLine($"Tweaked error (early exit): {bestError}");
                    return best;
                }

                for (int w = 0; w < wavelets.Length; w++)
                {
                    img.ApplyWavelets(new[] { wavelets[w] }, invert: true);
                    int[] vector = new[] { 0, 0, 0, 0, 0, 0 };
                    for (int v = 0; v < vector.Length; v++)
                    {
                        int multiplier = 1;
                        while (true)
                        {
                            vector[v] = multiplier;
                            wavelets[w].ApplyVector(vector, 0, false);
                            var newError = TotalRmsError(wavelets[w], img, target);
                            if (newError < bestError)
                            {
                                bestError = newError;
                                best = wavelets.Select(x => x.Clone()).ToArray();
                                dumpProgress(bestError, initial, best);
                                multiplier *= 2;
                            }
                            else if (multiplier != 1 && multiplier != -1)
                            {
                                wavelets = best.Select(x => x.Clone()).ToArray();
                                multiplier /= 2;
                            }
                            else if (multiplier == 1)
                            {
                                wavelets = best.Select(x => x.Clone()).ToArray();
                                multiplier = -1;
                            }
                            else
                            {
                                wavelets = best.Select(x => x.Clone()).ToArray();
                                break;
                            }
                        }
                        vector[v] = 0;
                    }
                    img.ApplyWavelets(new[] { wavelets[w] });
                }
                // Re-evaluate fully because the inner loop's error evaluation is subject to floating point errors compared to the initial error
                img = initial.Clone();
                img.ApplyWavelets(best);
                bestError = TotalRmsError(img, target);
                Console.WriteLine($"Tweaked error: {bestError}");
                if (!(bestError < initialError))
                    return best;
            }
        }

        public static Wavelet[] OptimizeWavelets(Wavelet[] wavelets, Surface initial, Surface target, int iterations, double sizeLimit)
        {
            var best = wavelets.Select(w => w.Clone()).ToArray();
            var bestError = TotalRmsError(best, initial, target);
            Console.Write($"OptimizeWavelets initial error {bestError}...");
            for (int iter = 0; iter < iterations; iter++)
            {
                int[] vector = Enumerable.Range(0, 6 * wavelets.Length).Select(_ => Rnd.Next(-8, 8 + 1)).ToArray();
                wavelets = best.Select(w => w.Clone()).ToArray();
                var curBestError = TotalRmsError(wavelets, initial, target);
                var curBest = wavelets;
                int multiplier = 1;
                while (true)
                {
                    for (int i = 0; i < wavelets.Length; i++)
                        for (int mul = 1; mul <= Math.Abs(multiplier); mul++)
                            wavelets[i].ApplyVector(vector, i * 6, negate: multiplier < 0);
                    if (wavelets.Any(w => w.W < sizeLimit * 4 || w.H < sizeLimit * 4))
                        break;
                    var newError = TotalRmsError(wavelets, initial, target);
                    if (newError < curBestError)
                    {
                        curBestError = newError;
                        curBest = wavelets.Select(w => w.Clone()).ToArray();
                        multiplier *= 2;
                    }
                    else if (multiplier != 1 && multiplier != -1)
                    {
                        wavelets = curBest.Select(w => w.Clone()).ToArray();
                        multiplier /= 2;
                    }
                    else if (multiplier == 1)
                    {
                        wavelets = curBest.Select(w => w.Clone()).ToArray();
                        multiplier = -1;
                    }
                    else
                        break;
                }
                if (curBestError < bestError)
                {
                    best = curBest.Select(w => w.Clone()).ToArray();
                    bestError = curBestError;
                }
            }
            Console.WriteLine($"final error {bestError}");
            return best;
        }

        private double _lastDumpProgressError = double.NaN;
        private void dumpProgress(double error, Surface initial, Wavelet[] wavelets)
        {
            if (double.IsNaN(_lastDumpProgressError) || (_lastDumpProgressError / error > 1.001))
            {
                _lastDumpProgressError = error;
                var img = initial.Clone();
                img.ApplyWavelets(wavelets);
                img.Save($"dump-progress-{error:000000.0000}-{img.WaveletCount:000}.png");
            }
        }

        public static double TotalRmsError(Surface current, Surface target)
        {
            double total = 0;
            for (int y = 0; y < target.Height; y++)
                for (int x = 0; x < target.Width; x++)
                {
                    var pixel = current[x, y].Clip(-0.5, 255.5) - target[x, y];
                    total += pixel * pixel;
                }
            return Math.Sqrt(total);
        }

        private static Surface rmsTemp; // kinda hacky but this is a single-threaded code so it's not too bad...
        public static double TotalRmsError(IEnumerable<Wavelet> wavelets, Surface initial, Surface target)
        {
            if (rmsTemp == null)
                rmsTemp = new Surface(target.Width, target.Height);
            initial.CopyTo(rmsTemp);
            rmsTemp.ApplyWavelets(wavelets);
            return TotalRmsError(rmsTemp, target);
        }

        public static double TotalRmsError(Wavelet wavelet, Surface initial, Surface target)
        {
            wavelet.Precalculate();
            double total = 0;
            for (int y = 0; y < target.Height; y++)
                for (int x = 0; x < target.Width; x++)
                {
                    double pixel = initial[x, y];
                    if (x >= wavelet.MinX && x <= wavelet.MaxX && y >= wavelet.MinY && y <= wavelet.MaxY)
                        pixel += wavelet.Calculate(x, y);
                    pixel = pixel.Clip(-0.5, 255.5) - target[x, y];
                    total += pixel * pixel;
                }
            return Math.Sqrt(total);
        }

        public static void EncodeWavelets(string filename, IEnumerable<Wavelet> wavelets, int width, int height)
        {
            using (var file = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                file.WriteUInt32Optim((uint) width);
                file.WriteUInt32Optim((uint) height);
                file.WriteUInt32Optim((uint) wavelets.Count());
                foreach (var wvl in wavelets)
                    file.WriteUInt32Optim((uint) wvl.X);
                foreach (var wvl in wavelets)
                    file.WriteUInt32Optim((uint) wvl.Y);
                foreach (var wvl in wavelets)
                    file.WriteUInt32Optim((uint) wvl.W);
                foreach (var wvl in wavelets)
                    file.WriteUInt32Optim((uint) wvl.H);
                foreach (var wvl in wavelets)
                    file.WriteUInt32Optim((uint) wvl.A);
                foreach (var wvl in wavelets)
                    file.WriteInt32Optim(wvl.Brightness);
            }
        }

        public static void EncodeResiduals(string filename, Surface target, List<Wavelet> wavelets, int tolerance = 0)
        {
            var residuals = new Surface(target.Width, target.Height);
            residuals.ApplyWavelets(wavelets);
            residuals.Merge(target, (ip, op) => Math.Round(ip).Clip(0, 255) - op);
            //residuals.Process(p => Math.Abs(p) <= 3 ? 0 : 255);
            //residuals.Save("residuals3.png");
            //return;
            var symbols = residuals.Data.Select(p =>
            {
                int symbol = (int) p; // [-255, 255]
                if (Math.Abs(symbol) <= tolerance)
                    return 0;
                // transform so that the symbols code for 0 [0], 1 [1], -1 [2], 2 [3], -2 [4], 3 [5], -3 [6] etc in this order
                if (symbol > 0)
                    return symbol * 2 - 1;
                else
                    return -symbol * 2;
            }).ToArray();

            var tweak1 = 5.0;
            var tweak2 = 10.0;
            byte[] best = encodeResiduals(tweak1, tweak2, symbols);
            var bestTweak1 = tweak1;
            var bestTweak2 = tweak2;
            var dir1 = 0.1;
            var dir2 = 0.1;
            int noImprovementCount = 0;
            while (noImprovementCount < 50)
            {
                tweak1 += dir1;
                tweak2 += dir2;
                var bytes = encodeResiduals(tweak1, tweak2, symbols);
                if (best == null || bytes.Length < best.Length)
                {
                    best = bytes;
                    bestTweak1 = tweak1;
                    bestTweak2 = tweak2;
                    dir1 *= 1.5;
                    dir2 *= 1.5;
                    noImprovementCount = 0;
                }
                else
                {
                    dir1 = Rnd.NextDouble(-0.1, 0.1);
                    dir2 = Rnd.NextDouble(-0.1, 0.1);
                    tweak1 = bestTweak1;
                    tweak2 = bestTweak2;
                    noImprovementCount++;
                }
            }
            using (var file = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bs = new BinaryStream(file))
            {
                bs.WriteDouble(bestTweak1);
                bs.WriteDouble(bestTweak2);
                bs.WriteUInt32Optim((uint) tolerance);
                for (int i = 0; i < 20; i++)
                    bs.WriteUInt32Optim((uint) symbols.Count(s => s == i));
                file.Write(best);
            }
        }

        private static byte[] encodeResiduals(double tweak1, double tweak2, int[] symbols)
        {
            // symbols range from -255 to 255, shifted by 255: [0, 510]
            var frequencies = Enumerable.Range(0, 511).Select(x => (ulong) (100000 * Math.Exp(-tweak1 * (x / 511.0 * tweak2)))).Select(x => x < 1 ? 1 : x).ToArray();
            for (int i = 0; i < 20; i++)
                frequencies[i] = (ulong) symbols.Count(s => s == i);
            using (var ms = new MemoryStream())
            {
                var arith = new ArithmeticCodingWriter(ms, frequencies);
                foreach (var symbol in symbols)
                    arith.WriteSymbol(symbol);
                arith.Close(false);
                return ms.ToArray();
            }
        }

        public static void EncodeResidualsCec(string filename, Surface target, List<Wavelet> wavelets, int tolerance = 0)
        {
            var residuals = new Surface(target.Width, target.Height);
            residuals.ApplyWavelets(wavelets);
            residuals.Merge(target, (ip, op) => Math.Round(ip).Clip(0, 255) - op);
            //residuals.Process(p => Math.Abs(p) <= 3 ? 0 : 255);
            //residuals.Save("residuals3.png");
            //return;
            var symbols = residuals.Data.Select(p =>
            {
                int symbol = (int) p; // [-255, 255]
                if (Math.Abs(symbol) <= tolerance)
                    return 0;
                // transform so that the symbols code for 0 [0], 1 [1], -1 [2], 2 [3], -2 [4], 3 [5], -3 [6] etc in this order
                if (symbol > 0)
                    return symbol * 2 - 1;
                else
                    return -symbol * 2;
            }).ToArray();

            var bestThresh1 = -1;
            var bestThresh2 = -1;
            byte[] best = null;
            for (int thresh1 = 2; thresh1 < 10; thresh1++)
                for (int thresh2 = 2; thresh2 < 20; thresh2++)
                {
                    var enc = encodeResidualsCecBlock(symbols, target.Width, target.Height, thresh1, thresh2);
                    if (best == null || enc.Length < best.Length)
                    {
                        best = enc;
                        bestThresh1 = thresh1;
                        bestThresh2 = thresh2;
                    }
                }
            using (var file = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bs = new BinaryStream(file))
            {
                bs.WriteUInt32Optim((uint) tolerance);
                bs.WriteUInt32Optim((uint) bestThresh1);
                bs.WriteUInt32Optim((uint) bestThresh2);
                file.Write(best);
            }
        }

        private static byte[] encodeResidualsCecBlock(int[] symbols, int width, int height, int maxSubdivThresh, int typSubdivThresh)
        {
            ulong[] frequenciesPixels = RT.Util.Ut.NewArray(511, _ => 1UL);
            ulong[] frequenciesBlocks = RT.Util.Ut.NewArray(5, _ => 1UL);
            var ms = new MemoryStream();
            var arith = new ArithmeticCodingWriter(ms, frequenciesBlocks);

            void writeBlockTypeSymbol(int symbol)
            {
                arith.TweakProbabilities(frequenciesBlocks);
                arith.WriteSymbol(symbol);
                frequenciesBlocks[symbol]++;
            }

            void writePixelSymbol(int symbol)
            {
                arith.TweakProbabilities(frequenciesPixels);
                arith.WriteSymbol(symbol);
                frequenciesPixels[symbol]++;
            }

            bool allZeroes(int bx, int by, int bw, int bh)
            {
                for (int y = by; y < by + bh; y++)
                    for (int x = bx; x < bx + bw; x++)
                        if (symbols[y * width + x] != 0)
                            return false;
                return true;
            }

            bool allNonZeroes(int bx, int by, int bw, int bh)
            {
                for (int y = by; y < by + bh; y++)
                    for (int x = bx; x < bx + bw; x++)
                        if (symbols[y * width + x] == 0)
                            return false;
                return true;
            }

            void encodeBlock(int bx, int by, int bw, int bh, bool couldBeAllZeroes)
            {
                // Each block begins with a block type symbol.
                // 0 = the entire block is zeroes. 1 = output the entire block's pixels. 2 = subdivide into 4. 3 = subdivide into 2 vertically. 4 = subdivide into 2 horizontally.

                // Is the entire block zero pixels? Then output 0.
                if (couldBeAllZeroes && allZeroes(bx, by, bw, bh)) // couldBeAllZeroes is just a perf. optimization, because sometimes we already know this
                {
                    writeBlockTypeSymbol(0); // current block = all zeroes
                    return;
                }

                bool canSubdivideVert = bw >= maxSubdivThresh;
                bool canSubdivideHorz = bh >= maxSubdivThresh;

                // Can we subdivide vertically with one half being entirely zeroes? Then do so.
                if (canSubdivideVert)
                {
                    var bw1 = bw / 2;
                    var bw2 = bw - bw1;
                    if (allZeroes(bx, by, bw1, bh))
                    {
                        // First block is all zeroes. We're doing this.
                        writeBlockTypeSymbol(3); // current block = vertical subdivision
                        writeBlockTypeSymbol(0); // encode first block directly; no need to recurse
                        encodeBlock(bx + bw1, by, bw2, bh, false); // encode second block
                        return;
                    }
                    if (allZeroes(bx + bw1, by, bw2, bh))
                    {
                        // Second block is all zeroes. We're doing this.
                        writeBlockTypeSymbol(3); // current block = vertical subdivision
                        encodeBlock(bx, by, bw1, bh, false); // encode first block
                        writeBlockTypeSymbol(0); // encode second block directly; no need to recurse
                        return;
                    }
                }

                // Can we subdivide horizontally with one half being entirely zeroes? Then do so.
                if (canSubdivideHorz)
                {
                    var bh1 = bh / 2;
                    var bh2 = bh - bh1;
                    if (allZeroes(bx, by, bw, bh1))
                    {
                        // First block is all zeroes. We're doing this.
                        writeBlockTypeSymbol(4); // current block = horizontal subdivision
                        writeBlockTypeSymbol(0); // encode first block directly; no need to recurse
                        encodeBlock(bx, by + bh1, bw, bh2, false); // encode second block;
                        return;
                    }
                    if (allZeroes(bx, by + bh1, bw, bh2))
                    {
                        // Second block is all zeroes. We're doing this.
                        writeBlockTypeSymbol(4); // current block = horizontal subdivision
                        encodeBlock(bx, by, bw, bh1, false); // encode first block
                        writeBlockTypeSymbol(0); // encode second block directly; no need to recurse
                        return;
                    }
                }

                // If all pixels are non-zero, encode directly
                if (allNonZeroes(bx, by, bw, bh))
                {
                    writeBlockTypeSymbol(1); // current block = full block as pixels
                    for (int y = by; y < by + bh; y++)
                        for (int x = bx; x < bx + bw; x++)
                            writePixelSymbol(symbols[y * width + x]);
                    return;
                }

                // We can't cut off an entire half of the block; at this point it might make sense to encode pixels directly instead of subdividing, depending on block size
                canSubdivideVert = bw >= typSubdivThresh;
                canSubdivideHorz = bh >= typSubdivThresh;

                // Subdivide into 4, or into 2 if not possible, or output entire block's pixels if not possible.
                if (canSubdivideVert && canSubdivideHorz)
                {
                    var bw1 = bw / 2;
                    var bw2 = bw - bw1;
                    var bh1 = bh / 2;
                    var bh2 = bh - bh1;
                    writeBlockTypeSymbol(2); // current block = subdivide into 4
                    encodeBlock(bx, by, bw1, bh1, true);
                    encodeBlock(bx + bw1, by, bw2, bh1, true);
                    encodeBlock(bx, by + bh1, bw1, bh2, true);
                    encodeBlock(bx + bw1, by + bh1, bw2, bh2, true);
                }
                else if (canSubdivideVert)
                {
                    var bw1 = bw / 2;
                    var bw2 = bw - bw1;
                    writeBlockTypeSymbol(3); // current block = vertical subdivision
                    encodeBlock(bx, by, bw1, bh, true);
                    encodeBlock(bx + bw1, by, bw2, bh, true);
                }
                else if (canSubdivideHorz)
                {
                    var bh1 = bh / 2;
                    var bh2 = bh - bh1;
                    writeBlockTypeSymbol(4); // current block = horizontal subdivision
                    encodeBlock(bx, by, bw, bh1, true);
                    encodeBlock(bx, by + bh1, bw, bh2, true);
                }
                else
                {
                    writeBlockTypeSymbol(1); // current block = full block as pixels
                    for (int y = by; y < by + bh; y++)
                        for (int x = bx; x < bx + bw; x++)
                            writePixelSymbol(symbols[y * width + x]);
                }
            }

            encodeBlock(0, 0, width, height, true);
            arith.Close(false);
            return ms.ToArray();
        }
    }

    class Surface
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double[] Data { get; private set; }
        public int WaveletCount { get; private set; } = 0;

        public Surface(int width, int height)
        {
            Width = width;
            Height = height;
            Data = new double[Width * Height];
        }

        public unsafe Surface(BitmapBase img)
        {
            Width = img.Width;
            Height = img.Height;
            Data = new double[Width * Height];
            using (img.UseRead())
                for (int y = 0; y < Height; y++)
                {
                    byte* end = img.Data + y * img.Stride + 4 * Width;
                    int x = 0;
                    for (byte* data = img.Data + y * img.Stride; data < end; x++, data += 4)
                        Data[y * Width + x] = *data;
                }
        }

        public double this[int x, int y]
        {
            get => Data[y * Width + x];
            set { Data[y * Width + x] = value; }
        }

        public unsafe void Save(string path)
        {
            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                var bmpData = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                for (int y = 0; y < Height; y++)
                {
                    byte* data = ((byte*) bmpData.Scan0) + y * bmpData.Stride;
                    byte* end = data + 3 * Width;
                    int x = 0;
                    while (data < end)
                    {
                        byte val = (byte) ((int) Math.Round(Data[y * Width + x])).Clip(0, 255);
                        *(data++) = val;
                        *(data++) = val;
                        *(data++) = val;
                        x++;
                    }
                }
                bmp.Save(path);
            }
        }

        public Surface Clone()
        {
            return new Surface(Width, Height) { Data = Data.ToArray(), WaveletCount = WaveletCount };
        }

        public void CopyTo(Surface target)
        {
            if (target.Width != Width || target.Height != Height)
                throw new ArgumentException("Target image must have the same width and height");
            Array.Copy(Data, target.Data, Data.Length);
        }

        public void ApplyWavelets(IEnumerable<Wavelet> wavelets, bool invert = false)
        {
            double mul = invert ? -1 : 1;
            foreach (var wavelet in wavelets)
            {
                WaveletCount += invert ? -1 : 1;
                wavelet.Precalculate();
                int xStart = (wavelet.X / 4).Clip(0, Width - 1);
                int yStart = (wavelet.Y / 4).Clip(0, Height - 1);
                // It's possible for the clipped starting point to be outside the ellipse, which would make the entire wavelet invisible even if a part of the ellipse
                // actually protrudes into the image. This is OK though, the optimizer will just have to avoid such placements.

                int minX = xStart;
                int maxX = xStart;
                int yDir = -1;
                int y = yStart;
                double val;
                while (true)
                {
                    int x = minX;
                    val = wavelet.Calculate(x, y) * mul;
                    bool hadNonZero = val != 0;
                    if (hadNonZero)
                    {
                        this[x, y] += val;
                        // move left from minX
                        while (true)
                        {
                            x--;
                            if (x < 0)
                                break;
                            val = wavelet.Calculate(x, y) * mul;
                            if (val == 0)
                                break;
                            this[x, y] += val;
                        }
                        var wasMinX = minX;
                        minX = x + 1; // last X that wasn't zero
                        x = wasMinX;
                    }
                    // move right from original minX
                    // hadNonZero continues to indicate whether we're yet to find the first non-zero pixel on this right-directed scan
                    while (true)
                    {
                        x++; // we've already processed the pixel at X
                        if (x >= Width)
                            break;
                        val = wavelet.Calculate(x, y) * mul;
                        if (val == 0)
                        {
                            if (!hadNonZero)
                                continue;
                            break;
                        }
                        if (!hadNonZero)
                        {
                            hadNonZero = true;
                            minX = x;
                        }
                        this[x, y] += val;
                    }
                    maxX = x - 1; // last X that wasn't zero

                    if (hadNonZero)
                    {
                        y += yDir;
                    }
                    if (!hadNonZero || y < 0 || y >= Height)
                    {
                        // we're done with the current Y direction
                        if (yDir == -1)
                        {
                            yDir = 1;
                            y = yStart + 1;
                            minX = xStart;
                            maxX = xStart;
                            if (y >= Height)
                                break; // we're done with the whole thing
                        }
                        else
                            break; // we're done with the whole thing
                    }
                }
            }
        }

        public void Merge(Surface surf, Func<double, double, double> merge)
        {
            if (Width != surf.Width || Height != surf.Height)
                throw new ArgumentException();
            for (int i = 0; i < Data.Length; i++)
                Data[i] = merge(Data[i], surf.Data[i]);
        }

        public void Process(Func<double, double> process)
        {
            for (int i = 0; i < Data.Length; i++)
                Data[i] = process(Data[i]);
        }

        public void Blur3()
        {
            var newData = new double[Data.Length];
            // pretty inefficient but good enough for now
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int count = 0;
                    double pix = 0;
                    if (x > 0)
                    {
                        if (y > 0)
                        {
                            pix += this[x - 1, y - 1];
                            count++;
                        }
                        pix += this[x - 1, y];
                        count++;
                        if (y < Height - 1)
                        {
                            pix += this[x - 1, y + 1];
                            count++;
                        }
                    }
                    if (y > 0)
                    {
                        pix += this[x, y - 1];
                        count++;
                    }
                    pix += this[x, y];
                    count++;
                    if (y < Height - 1)
                    {
                        pix += this[x, y + 1];
                        count++;
                    }
                    if (x < Width - 1)
                    {
                        if (y > 0)
                        {
                            pix += this[x + 1, y - 1];
                            count++;
                        }
                        pix += this[x + 1, y];
                        count++;
                        if (y < Height - 1)
                        {
                            pix += this[x + 1, y + 1];
                            count++;
                        }
                    }

                    newData[y * Width + x] = pix / count;
                }
            Data = newData;
        }
    }

    class Wavelet
    {
        public int Brightness;
        public int X, Y, W, H, A;
        public int MinX, MinY, MaxX, MaxY;

        private bool _precalculated = false;
        private double mXX, mXY, mYX, mYY, mXO, mYO;

        public override string ToString() { return $"X={X}; Y={Y}; W={W}; H={H}; A={A}; B={Brightness}"; }

        public Wavelet()
        {
        }

        public Wavelet(string line)
        {
            var parts = line.Split(';').Select(s => int.Parse(s.Trim().Substring(2))).ToArray();
            X = parts[0];
            Y = parts[1];
            W = parts[2];
            H = parts[3];
            A = parts[4];
            Brightness = parts[5];
        }

        public void Invalidate()
        {
            // to be called after modifying X/Y/W/H/A - currently entirely manual and error-prone... TODO
            _precalculated = false;
        }

        public void Precalculate()
        {
            if (_precalculated)
                return;
            var cos = Math.Cos(A / 180.0 * Math.PI);
            var sin = Math.Sin(A / 180.0 * Math.PI);
            mXX = cos * 4.0 / W;
            mXY = sin * 4.0 / W;
            mXO = -(X * cos + Y * sin) / W;
            mYX = -sin * 4.0 / H;
            mYY = cos * 4.0 / H;
            mYO = (X * sin - Y * cos) / H;

            var size = W > H ? W : H;
            MinX = (X - size - 3) >> 2;
            MaxX = (X + size + 3) >> 2;
            MinY = (Y - size - 3) >> 2;
            MaxY = (Y + size + 3) >> 2;

            _precalculated = true;
        }

        public double Calculate(int x, int y)
        {
            double tx = x * mXX + y * mXY + mXO;
            double ty = x * mYX + y * mYY + mYO;
            double lengthSquared = tx * tx + ty * ty;

            if (lengthSquared > 1)
                return 0;
            return Brightness * Math.Exp(-lengthSquared * 6.238324625039); // square of the length of (tx, ty), conveniently cancelling out the sqrt
            // 6.23... = ln 512, ie the point at which the value becomes less than 0.5 when scaled by 256, ie would round to 0
        }

        public Wavelet Clone()
        {
            return new Wavelet { X = X, Y = Y, W = W, H = H, A = A, Brightness = Brightness };
        }

        public void ApplyVector(int[] vector, int offset, bool negate)
        {
            int mul = negate ? -1 : 1;
            X = (X + vector[offset + 0] * mul);
            Y = (Y + vector[offset + 1] * mul);
            W = (W + vector[offset + 2] * mul);
            H = (H + vector[offset + 3] * mul);
            A = (A + vector[offset + 4] * mul + 360) % 360;
            Brightness = (Brightness + vector[offset + 5] * mul).Clip(-260, 260);
            if (X < 1) X = 1;
            if (Y < 1) Y = 1;
            if (W < 1) W = 1;
            if (W > 99999) W = 99999;
            if (H < 1) H = 1;
            if (H > 99999) H = 99999;
            if (W < H / 4) W = H / 4;
            if (H < W / 4) H = W / 4;
            _precalculated = false;
        }

        public void BoundingBoxPrecise(out int minX, out int minY, out int maxX, out int maxY)
        {
            var sinSq = Math.Sin(A / 180.0 * Math.PI);
            sinSq *= sinSq;
            var cosSq = Math.Cos(A / 180.0 * Math.PI);
            cosSq *= cosSq;
            var wSq = (double) W * W / 16.0;
            var hSq = (double) H * H / 16.0;
            var dx = Math.Sqrt(wSq * cosSq + hSq * sinSq);
            var dy = Math.Sqrt(wSq * sinSq + hSq * cosSq);
            minX = (int) Math.Floor(X / 4.0 - dx); // -1 just to be extra safe with off-by-one
            maxX = (int) Math.Ceiling(X / 4.0 + dx);
            minY = (int) Math.Floor(Y / 4.0 - dy);
            maxY = (int) Math.Floor(Y / 4.0 + dy);
        }

        public double Area()
        {
            return W / 8.0 * H / 8.0 * Math.PI;
        }
    }
}
