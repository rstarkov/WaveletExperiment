﻿using System;
using System.Collections.Generic;
using System.Drawing;
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
                    ApplyWavelets(_curImage, new[] { wavelet });
                    _curTotalAreaCovered += wavelet.W / 4.0 * wavelet.H / 4.0;
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

                    ApplyWavelets(_imageAtScaleStart, finals);
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
            _finalWavelets = File.ReadAllLines(path).Select(l => l.Replace("FINAL: ", "").Trim()).Where(l => l.StartsWith("X=")).Select(l => new Wavelet(l)).ToList();
            _curScaleWavelets = new List<Wavelet>();
            _curTotalAreaCovered = 0;
            _curScale = scale;
            _curImage = new Surface(_targetImage.Width, _targetImage.Height);
            ApplyWavelets(_curImage, _finalWavelets);
            _imageAtScaleStart = _curImage.Clone();
            dump(_curImage, "-loaded");
        }

        public void Optimize()
        {
            var wasError = TotalRmsError(new Wavelet[0], _curImage, _targetImage);
            var wavelets = ChooseWavelets(_curImage, _targetImage, (int) Math.Round(_curScale));
            var newError = TotalRmsError(wavelets, _curImage, _targetImage);
            if (wasError == newError)
            {
                Console.WriteLine("Error didn't change; a bad wavelet must have been optimized off the screen");
                return;
            }
            ApplyWavelets(_curImage, wavelets);
            foreach (var wavelet in wavelets)
            {
                _curTotalAreaCovered += wavelet.W / 4.0 * wavelet.H / 4.0;
                _curScaleWavelets.Add(wavelet);
            }
            File.AppendAllLines("wavelets.txt", wavelets.Select(w => w.ToString()));
            File.AppendAllLines("wavelets.txt", new[] { $"RMS error at {_finalWavelets.Count + _curScaleWavelets.Count} wavelets: {newError}" });
            dump(_curImage);
            if (_curTotalAreaCovered > 2 * _targetImage.Width * _targetImage.Height)
            {
                var final = TweakWavelets(_curScaleWavelets.ToArray(), _imageAtScaleStart, _targetImage);
                _finalWavelets.AddRange(final);
                ApplyWavelets(_imageAtScaleStart, final);
                _curScaleWavelets.Clear();
                _curImage = _imageAtScaleStart.Clone();
                var finalError = TotalRmsError(new Wavelet[0], _imageAtScaleStart, _targetImage);
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
            ApplyWavelets(_imageAtScaleStart, _finalWavelets);
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
                ApplyWavelets(img, wavelets);
                var curError = TotalRmsError(new Wavelet[0], img, target);
                for (int w = 0; w < wavelets.Length; w++)
                {
                    ApplyWavelets(img, new[] { wavelets[w] }, invert: true);
                    int[] vector = new[] { 0, 0, 0, 0, 0, 0 };
                    for (int v = 0; v < vector.Length; v++)
                    {
                        int multiplier = 1;
                        while (true)
                        {
                            vector[v] = multiplier;
                            wavelets[w].ApplyVector(vector, 0, false);
                            var newError = TotalRmsError(new[] { wavelets[w] }, img, target);
                            if (curError > newError)
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
                    ApplyWavelets(img, new[] { wavelets[w] });
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
            ApplyWavelets(img, wavelets);
            dump(img);
        }

        private static void ApplyWavelets(Surface img, IEnumerable<Wavelet> wavelets, bool invert = false)
        {
            double mul = invert ? -1 : 1;
            foreach (var wavelet in wavelets)
                wavelet.Precalculate();
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    double pixel = img[x, y];
                    foreach (var wavelet in wavelets)
                        pixel += wavelet.Calculate(x, y) * mul;
                    img[x, y] = pixel;
                }
        }

        private static double TotalRmsError(Wavelet[] wavelets, Surface initial, Surface target)
        {
            foreach (var wavelet in wavelets)
                wavelet.Precalculate();
            double total = 0;
            for (int y = 0; y < target.Height; y++)
                for (int x = 0; x < target.Width; x++)
                {
                    double pixel = initial[x, y];
                    foreach (var wavelet in wavelets)
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

        public unsafe BitmapRam ToBitmapRam()
        {
            var result = new BitmapRam(Width, Height);
            using (result.UseWrite())
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* end = result.Data + y * result.Stride + 4 * Width;
                    int x = 0;
                    for (byte* data = result.Data + y * result.Stride; data < end; x++)
                    {
                        byte val = (byte) ((int) Math.Round(Data[y * Width + x])).Clip(0, 255);
                        *(data++) = val;
                        *(data++) = val;
                        *(data++) = val;
                        *(data++) = 0xFF;
                    }
                }
            }
            return result;
        }

        public void Save(string path)
        {
            ToBitmapRam().ToBitmapGdi().Bitmap.Save(path);
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
    }

    class Wavelet
    {
        public int Brightness;
        public int X, Y, W, H, A;

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

        public void Precalculate()
        {
            if (W < H / 4)
                W = H / 4;
            if (H < W / 4)
                H = W / 4;
            var cos = Math.Cos(A / 180.0 * Math.PI);
            var sin = Math.Sin(A / 180.0 * Math.PI);
            mXX = cos * 4.0 / W;
            mXY = sin * 4.0 / W;
            mXO = -(X * cos + Y * sin) / W;
            mYX = -sin * 4.0 / H;
            mYY = cos * 4.0 / H;
            mYO = (X * sin - Y * cos) / H;
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
            X = (X + vector[offset + 0] * mul).ClipMin(1);
            Y = (Y + vector[offset + 1] * mul).ClipMin(1);
            W = (W + vector[offset + 2] * mul).ClipMin(1);
            H = (H + vector[offset + 3] * mul).ClipMin(1);
            A = (A + vector[offset + 4] * mul + 360) % 360;
            Brightness = (Brightness + vector[offset + 5] * mul).Clip(-260, 260);
        }
    }
}
