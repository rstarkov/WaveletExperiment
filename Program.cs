using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;
using TankIconMaker;

/// <summary>
/// - scale should be a running average of recent best wavelets so that the RNG is centered around the average
/// --- but need to decide when to tweak "current scale" once there is no longer a clear time for that
/// - breadth-first tweaking? where we find the most responsive parameter to tweak and tweak it first
/// - optimize should carry on as long as it's able to find reasonably good improvements
/// - end Tweak when improvements are too small
/// - multi-threaded Tweak
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

            return;

            var opt = new Optimizer(target);
            while (true)
                opt.Optimize();
        }
    }

    class Optimizer
    {
        private double _curScale;
        private double _curTotalAreaCovered;
        private Surface _curImage, _targetImage;
        private Surface _imageAtScaleStart;
        private List<Wavelet> _finalWavelets = new List<Wavelet>();
        private List<Wavelet> _curScaleWavelets;
        private int dumpIndex = 0;

        public Optimizer(Surface target)
        {
            _targetImage = target;
            _curImage = new Surface(target.Width, target.Height);
            _curScale = target.Width * 4;
            _curTotalAreaCovered = 0;
            _imageAtScaleStart = _curImage.Clone();
            _curScaleWavelets = new List<Wavelet>();
        }

        public Optimizer(Surface target, string loadFrom) : this(target)
        {
            var lines = File.ReadAllLines(loadFrom);
            var finals = new List<Wavelet>();
            foreach (var line in lines)
            {
                if (line.StartsWith("X"))
                {
                    var wavelet = new Wavelet(line);
                    _curImage.ApplyWavelets(new[] { wavelet });
                    _curTotalAreaCovered += wavelet.Area();
                    _curScaleWavelets.Add(wavelet);
                }
                else if (line.StartsWith("FINAL: X"))
                {
                    finals.Add(new Wavelet(line.Substring("FINAL: ".Length)));
                }
                else if (line.StartsWith("Scale: "))
                {
                    int finalsCount = (int) Math.Sqrt(finals.Count);
                    RT.Util.Ut.Assert(finalsCount * finalsCount == finals.Count);
                    finals = finals.Take(finalsCount).ToList();
                    _finalWavelets.AddRange(finals);

                    _imageAtScaleStart.ApplyWavelets(finals);
                    _curScaleWavelets.Clear();
                    _curImage = _imageAtScaleStart.Clone();
                    _curScale *= 0.70;
                    _curTotalAreaCovered = 0;
                    RT.Util.Ut.Assert(line == $"Scale: {_curScale / 4.0:0.00}");

                    finals.Clear();
                }
            }
            dump(_imageAtScaleStart, "-loaded-at-start");
            _curScaleWavelets.Clear();
            _curImage = _imageAtScaleStart.Clone();
            _curTotalAreaCovered = 0;
        }

        public void LoadWavelets(string path, double scale)
        {
            LoadWavelets(
                File.ReadAllLines(path).Select(l => l.Replace("FINAL: ", "").Trim()).Where(l => l.StartsWith("X=")).Select(l => new Wavelet(l)).ToList(),
                scale);
        }

        public void LoadWavelets(IEnumerable<Wavelet> wavelets, double scale)
        {
            _finalWavelets = wavelets.ToList();
            _curScaleWavelets = new List<Wavelet>();
            _curTotalAreaCovered = 0;
            _curScale = scale;
            _curImage = new Surface(_targetImage.Width, _targetImage.Height);
            _curImage.ApplyWavelets(_finalWavelets);
            _imageAtScaleStart = _curImage.Clone();
            dump(_curImage, "-loaded");
        }

        public void Optimize()
        {
            var wasError = TotalRmsError(_curImage, _targetImage);
            var wavelets = ChooseWavelets(_curImage, _targetImage, (int) Math.Round(_curScale));
            var newError = TotalRmsError(wavelets, _curImage, _targetImage);
            if (wasError == newError)
            {
                Console.WriteLine("Error didn't change; a bad wavelet must have been optimized off the screen");
                return;
            }
            _curImage.ApplyWavelets(wavelets);
            foreach (var wavelet in wavelets)
            {
                _curTotalAreaCovered += wavelet.Area();
                _curScaleWavelets.Add(wavelet);
            }
            File.AppendAllLines("wavelets.txt", wavelets.Select(w => w.ToString()));
            File.AppendAllLines("wavelets.txt", new[] { $"RMS error at {_finalWavelets.Count + _curScaleWavelets.Count} wavelets: {newError}" });
            dump(_curImage);
            if (_curTotalAreaCovered > 2 * _targetImage.Width * _targetImage.Height)
            {
                var final = TweakWavelets(_curScaleWavelets.ToArray(), _imageAtScaleStart, _targetImage);
                _finalWavelets.AddRange(final);
                _imageAtScaleStart.ApplyWavelets(final);
                _curScaleWavelets.Clear();
                _curImage = _imageAtScaleStart.Clone();
                var finalError = TotalRmsError(_imageAtScaleStart, _targetImage);
                File.AppendAllLines("wavelets.txt", final.Select(w => "FINAL: " + w.ToString()));
                File.AppendAllLines("wavelets.txt", new[] { $"RMS FINAL error at {_finalWavelets.Count} wavelets: {finalError}" });
                File.WriteAllLines("wavelets-final.txt", _finalWavelets.Select(w => "FINAL: " + w.ToString()));
                File.AppendAllLines("wavelets-final.txt", new[] { $"RMS FINAL error at {_finalWavelets.Count} wavelets: {finalError}" });
                dump(_imageAtScaleStart, "-tweaked");
                _curScale *= 0.70;
                _curTotalAreaCovered = 0;
                File.AppendAllLines("wavelets.txt", new[] { $"Scale: {_curScale / 4.0:0.00}" });
            }
        }

        public void TweakFinalWavelets()
        {
            _imageAtScaleStart = new Surface(_targetImage.Width, _targetImage.Height);
            _finalWavelets = TweakWavelets(_finalWavelets.ToArray(), _imageAtScaleStart, _targetImage).ToList();
            _imageAtScaleStart.ApplyWavelets(_finalWavelets);
        }

        private Wavelet[] ChooseWavelets(Surface initial, Surface target, int scale)
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
                    W = Rnd.Next(scale / 2, scale),
                    H = Rnd.Next(scale / 2, scale),
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
            best = OptimizeWavelets(best, initial, target, 150, scale / 6);
            best = TweakWavelets(best, initial, target);
            Console.WriteLine($"Best error: {bestError}");
            return best;
        }

        private static Wavelet[] TweakWavelets(Wavelet[] wavelets, Surface initial, Surface target)
        {
            var best = wavelets.Select(w => w.Clone()).ToArray();
            wavelets = best.Select(w => w.Clone()).ToArray();
            while (true)
            {
                bool improvements = false;
                var img = initial.Clone();
                img.ApplyWavelets(wavelets);
                var curError = TotalRmsError(img, target);
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
                            if (curError / newError > 1.00000001) // the floating point errors mean that our optimized loop produces marginally different results to the initial full evaluation at the top, causing infinite loops if we treat a last d.p. change is an improvement
                            {
                                curError = newError;
                                best = wavelets.Select(x => x.Clone()).ToArray();
                                improvements = true;
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
                Console.WriteLine($"Tweaked error: {curError}");
                if (!improvements)
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

        private void dump(Surface img, string suffix = "")
        {
            try
            {
                img.Save($"dump{dumpIndex:00000}{suffix}.png");
            }
            catch { }
            dumpIndex++;
        }
        private void dump(Surface initial, Wavelet[] wavelets)
        {
            var img = initial.Clone();
            img.ApplyWavelets(wavelets);
            dump(img);
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
            return new Surface(Width, Height) { Data = Data.ToArray() };
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
