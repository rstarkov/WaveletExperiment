using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using RT.Util;
using RT.Util.ExtensionMethods;
using TankIconMaker;

/// <summary>
/// - limited passes tweak after every wavelet
/// - allow narrower wavelets and full-tweak
/// - targeted drop of wavelets around high error areas
/// - high-pass filter the original image to find important pixels, encode / compress differences for those, then optimize wavelets based on compressed size
/// - sine wave modulated by the current gaussian wavelet described by three extra numbers: frequency, phase, min brightness
/// 
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
        }

        private double calcWaveletScale()
        {
            if (_allWavelets.Count == 0)
                return _targetImage.Width * 4;
            double result = Math.Max(_allWavelets[0].W / 4.0, _allWavelets[0].H / 4.0);
            foreach (var wvl in _allWavelets.Skip(1))
                result = 0.9 * result + 0.1 * Math.Max(wvl.W / 4.0, wvl.H / 4.0);
            return result;
        }

        public void Optimize()
        {
            var img = new Surface(_targetImage.Width, _targetImage.Height);
            img.ApplyWavelets(_allWavelets);
            var newWavelet = ChooseRandomWavelet(img, _targetImage, calcWaveletScale());
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

        private Wavelet ChooseRandomWavelet(Surface initial, Surface target, double scale)
        {
            Wavelet best = null;
            double bestError = TotalRmsError(initial, target);
            double startingError = bestError;
            Console.Write($"ChooseRandomWavelet initial error: {bestError}, scale {scale:0.0}...");
            for (int iter = 0; iter < 10000; iter++)
            {
                var wavelet = new Wavelet
                {
                    X = Rnd.Next(0, target.Width * 4),
                    Y = Rnd.Next(0, target.Height * 4),
                    W = Rnd.Next((int) (4 * scale / 2), (int) (4 * scale * 2)),
                    H = Rnd.Next((int) (4 * scale / 2), (int) (4 * scale * 2)),
                    A = Rnd.Next(0, 360),
                    Brightness = Rnd.Next(-255, 255 + 1),
                };
                var newError = TotalRmsError(wavelet, initial, target);
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
            var bestNew = OptimizeWavelets(best, initial, target, 150, scale / 6);
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

        private Wavelet[] TweakWavelets(Wavelet[] wavelets, Surface initial, Surface target)
        {
            var best = wavelets.Select(w => w.Clone()).ToArray();
            wavelets = best.Select(w => w.Clone()).ToArray();
            while (true)
            {
                var img = initial.Clone();
                img.ApplyWavelets(wavelets);
                var bestError = TotalRmsError(img, target);
                var initialError = bestError;
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

        private static Wavelet[] OptimizeWavelets(Wavelet[] wavelets, Surface initial, Surface target, int iterations, int sizeLimit)
        {
            var best = wavelets.Select(w => w.Clone()).ToArray();
            var bestError = TotalRmsError(best, initial, target);
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
                    if (wavelets.Any(w => w.W < sizeLimit || w.H < sizeLimit))
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
                best = curBest.Select(w => w.Clone()).ToArray();
                bestError = curBestError;
            }
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
