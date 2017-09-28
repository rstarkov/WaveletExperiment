using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace WaveletExperiment
{
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
            var newWavelet = ChooseRandomWavelet(img, _targetImage, scale, _allWavelets.Count > 20, scale < 50, _allWavelets.Count > 20 ? 2000 : 10000);
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

        private Wavelet ChooseRandomWavelet(Surface initial, Surface target, double scale, bool tweakEveryGuess, bool errorGuided, int iterations)
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
            for (int iter = 0; iter < iterations; iter++)
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
                    W = Rnd.Next((int) (4 * scale / 4), (int) (4 * scale * 2)),
                    H = Rnd.Next((int) (4 * scale / 4), (int) (4 * scale * 2)),
                    A = Rnd.Next(0, 360),
                    Brightness = Rnd.Next(-255, 255 + 1),
                };
                var newError = TotalRmsError(wavelet, initial, target);
                Console.Write($"{iter} ");
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
    }
}
