using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace WaveletExperiment;

class Optimizer
{
    private Surface _target;
    private int _color; // 0 = Y, 1 = Co, 2 = Cg
    private int _background;
    public List<Wavelet> AllWavelets = new List<Wavelet>();
    public ColorOptimizer ColorOpt; // null if only need to optimize a single plane

    public Optimizer(Surface target, int color)
    {
        if (color < 0 || color > 2) throw new Exception();
        _target = target;
        _color = color;
        _background = _color == 0 ? target.Average : 0;
    }

    public void LoadWavelets(string path, int scaleNum = 1, int scaleDenom = 1)
    {
        LoadWavelets(File.ReadAllLines(path).Select(l => l.Replace("FINAL: ", "").Trim()).Where(l => l.StartsWith("X=")).Select(l => new Wavelet(l)).ToList(), scaleNum, scaleDenom);
    }

    public void LoadWavelets(IEnumerable<Wavelet> wavelets, int scaleNum = 1, int scaleDenom = 1)
    {
        AllWavelets = wavelets.Select(w => { w.X = w.X * scaleNum / scaleDenom; w.Y = w.Y * scaleNum / scaleDenom; w.W = w.W * scaleNum / scaleDenom; w.H = w.H * scaleNum / scaleDenom; return w; }).ToList();
        _lastDumpProgressError = TotalRmsError(AllWavelets);
    }

    public void SaveWavelets(string path)
    {
        File.WriteAllLines(path, AllWavelets.Select(w => "FINAL: " + w.ToString()));
    }

    public Surface GetImage()
    {
        var img = new Surface(_target.Width, _target.Height, _background);
        img.ApplyWavelets(AllWavelets, _color);
        return img;
    }

    public Surface GetResiduals()
    {
        var img = GetImage();
        img.Merge(_target, (pixActual, pixTarget) => Math.Round(pixActual).Clip(0, 255) - pixTarget + 128);
        return img;
    }

    private double calcWaveletScale()
    {
        if (AllWavelets.Count <= 10)
            return _target.Width * 0.6;
        double result = Math.Max((AllWavelets[0].W / 4.0).ClipMax(_target.Width), (AllWavelets[0].H / 4.0).ClipMax(_target.Height));
        foreach (var wvl in AllWavelets.Skip(1))
            result = 0.9 * result + 0.1 * Math.Max((wvl.W / 4.0).ClipMax(_target.Width), (wvl.H / 4.0).ClipMax(_target.Height));
        return result;
    }

    public void OptimizeStep(bool save = true)
    {
        if (_color != 0) throw new Exception("This step might need tweaks before it will work on colors other than Y");
        var img = new Surface(_target.Width, _target.Height, _background);
        img.ApplyWavelets(AllWavelets, _color);
        var scale = calcWaveletScale();
        var newWavelet = ChooseRandomWavelet(img, scale, AllWavelets.Count > 20, scale < 50, AllWavelets.Count > 20 ? 2000 : 15000);
        AllWavelets.Add(newWavelet);

        if (AllWavelets.Count <= 10 || (AllWavelets.Count < 50 && AllWavelets.Count % 2 == 0) || (AllWavelets.Count < 200 && AllWavelets.Count % 5 == 0) || (AllWavelets.Count % 20 == 0))
        {
            Console.WriteLine("Tweak all...");
            TweakAllWavelets();
        }

        if (save)
        {
            var newError = TotalRmsError(AllWavelets);
            File.AppendAllLines("wavelets.txt", new[] { $"RMS error at {AllWavelets.Count} wavelets: {newError}. Total size: {getTotalSize():#,0} bytes" });
            SaveWavelets($"wavelets-{AllWavelets.Count:0000}.txt");
            File.AppendAllLines($"wavelets-{AllWavelets.Count:0000}.txt", new[] { $"RMS FINAL error at {AllWavelets.Count} wavelets: {newError}" });
        }
    }

    private long getTotalSize()
    {
        var ms = new MemoryStream();
        Codec.EncodeAll(ms, _target, AllWavelets);
        return ms.Length;
    }

    public void TweakAllWavelets()
    {
        var img = new Surface(_target.Width, _target.Height, _background);
        AllWavelets = TweakWavelets(AllWavelets.ToArray(), img).ToList();
    }

    public void TweakAllWaveletsForResiduals(int tolerance = 0)
    {
        if (_color != 0) throw new Exception("This step might need tweaks before it will work on colors other than Y");
        _lastDumpProgressError = 999999999;
        var img = new Surface(_target.Width, _target.Height, _background);
        AllWavelets = TweakWavelets(AllWavelets.ToArray(), img, eval: eval).ToList();

        double eval(Wavelet wvl, Surface cur)
        {
            if (wvl != null)
                cur.ApplyWavelets(new[] { wvl }, _color);
            var ms = new MemoryStream();
            Codec.EncodeResidualsIncremental(new DoNotCloseStream(ms), _target, cur, tolerance);
            var result = ms.Length;
            dumpProgress(result, cur, new Wavelet[0], true);
            if (wvl != null)
                cur.ApplyWavelets(new[] { wvl }, _color, invert: true);
            Console.Write($"{result:#,0}... ");
            return result;
        }
    }

    private Wavelet ChooseRandomWavelet(Surface initial, double scale, bool tweakEveryGuess, bool errorGuided, int iterations)
    {
        Surface errors = null;
        if (errorGuided)
        {
            errors = _target.Clone();
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
        double bestError = TotalRmsError(initial);
        double startingError = bestError;
        Console.Write($"ChooseRandomWavelet initial error: {bestError}, scale {scale:0.0}, {(errorGuided ? "error-guided" : "random")}...");
        for (int iter = 0; iter < iterations; iter++)
        {
            int x, y;
            while (true)
            {
                x = Rnd.Next(0, _target.Width);
                y = Rnd.Next(0, _target.Height);
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
            var newError = TotalRmsError(wavelet, initial);
            Console.Write($"{iter} ");
            if (tweakEveryGuess)
            {
                wavelet = TweakWavelets(new[] { wavelet }, initial, 10)[0];
                newError = TotalRmsError(wavelet, initial);
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
        best = TweakWavelets(new[] { best }, initial)[0];
        var doubleCheckError = TotalRmsError(best, initial);
        if (doubleCheckError >= startingError)
            throw new Exception("ChooseRandomWavelet: error check failed");
        return best;
    }

    private Wavelet[] TweakWavelets(Wavelet[] wavelets, Surface initial, int iterations = 0, Func<Wavelet, Surface, double> eval = null)
    {
        if (eval == null)
            eval = (Wavelet wvl, Surface cur) =>
            {
                if (wvl == null)
                    return TotalRmsError(cur);
                else
                    return TotalRmsError(wvl, cur);
            };

        var best = wavelets.Select(w => w.Clone()).ToArray();
        wavelets = best.Select(w => w.Clone()).ToArray();
        while (true)
        {
            var img = initial.Clone();
            img.ApplyWavelets(wavelets, color: _color);
            var bestError = eval(null, img);
            var initialError = bestError;

            if (iterations > 0)
                iterations--;
            if (iterations == 1)
            {
                //Console.WriteLine($"Tweaked error (early exit): {bestError}");
                return best;
            }

            for (int w = 0; w < wavelets.Length; w++)
            {
                img.ApplyWavelets(new[] { wavelets[w] }, invert: true, color: _color);
                int[] vector = _color == 0 ? new[] { 0, 0, 0, 0, 0, 0 } : new[] { 0 };
                for (int v = 0; v < vector.Length; v++)
                {
                    int multiplier = 1;
                    while (true)
                    {
                        vector[v] = multiplier;
                        wavelets[w].ApplyVector(vector, 0, false, _color);
                        var newError = eval(wavelets[w], img);
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
                img.ApplyWavelets(new[] { wavelets[w] }, color: _color);
            }
            // Re-evaluate fully because the inner loop's error evaluation is subject to floating point errors compared to the initial error
            img = initial.Clone();
            img.ApplyWavelets(best, color: _color);
            bestError = eval(null, img);
            //Console.WriteLine($"Tweaked error: {bestError}");
            if (!(bestError < initialError))
                return best;
        }
    }

    private double _lastDumpProgressError = double.NaN;
    private void dumpProgress(double error, Surface initial, Wavelet[] wavelets, bool anyImprovement = false)
    {
        if (ColorOpt != null)
            ColorOpt.Dump(error, initial, wavelets, anyImprovement, _color);
        else
        {
            // this looks weird in order to support dumping progress before a wavelet that's being tweaked has been fully committed to AllWavelets
            if (double.IsNaN(_lastDumpProgressError) || (_lastDumpProgressError / error > 1.001) || (anyImprovement && error < _lastDumpProgressError))
            {
                _lastDumpProgressError = error;
                var img = initial.Clone();
                img.ApplyWavelets(wavelets, _color);
                img.Save($"dump-progress-{error:000000.0000}-{img.WaveletCount:0000}.png");
            }
        }
    }

    public double TotalRmsError(Surface current)
    {
        double clipMin = _color == 0 ? -0.5 : -255.5;
        double total = 0;
        for (int y = 0; y < _target.Height; y++)
            for (int x = 0; x < _target.Width; x++)
            {
                var pixel = current[x, y].Clip(clipMin, 255.5) - _target[x, y];
                total += pixel * pixel;
            }
        return Math.Sqrt(total);
    }

    private Surface rmsTemp; // kinda hacky but this is a single-threaded code so it's not too bad...
    public double TotalRmsError(IEnumerable<Wavelet> wavelets, Surface initial = null)
    {
        if (initial == null)
            rmsTemp = new Surface(_target.Width, _target.Height, _background);
        else
        {
            rmsTemp ??= new Surface(_target.Width, _target.Height, 0 /* about to overwrite it anyway */);
            initial.CopyTo(rmsTemp);
        }
        rmsTemp.ApplyWavelets(wavelets, _color);
        return TotalRmsError(rmsTemp);
    }

    public unsafe double TotalRmsError(Wavelet wavelet, Surface initial)
    {
        // not using Surface.this[int, int] reduces this from 0.800ms to 0.315ms. Using double* reduces this to 0.260ms.
        int clr = _color == 0 ? wavelet.Brightness : _color == 1 ? wavelet.Co : _color == 2 ? wavelet.Cg : throw new Exception();
        double clipMin = _color == 0 ? -0.5 : -255.5;
        wavelet.Precalculate();
        double total = 0;
        fixed (double* initialPtrF = initial.Data, targetPtrF = _target.Data)
        {
            double* initialPtr = initialPtrF, targetPtr = targetPtrF;
            for (int y = 0; y < _target.Height; y++)
                for (int x = 0; x < _target.Width; x++)
                {
                    double pixel = *initialPtr;
                    if (x >= wavelet.MinX && x <= wavelet.MaxX && y >= wavelet.MinY && y <= wavelet.MaxY)
                        pixel += wavelet.Calculate(x, y) * clr;
                    pixel = pixel.Clip(clipMin, 255.5) - *targetPtr;
                    total += pixel * pixel;
                    initialPtr++;
                    targetPtr++;
                }
        }
        return Math.Sqrt(total);
    }
}

class ColorOptimizer
{
    public Optimizer OptY, OptCo, OptCg;
    public double RmsY, RmsCo, RmsCg;

    public ColorOptimizer((Surface Y, Surface Co, Surface Cg) target)
    {
        OptY = new Optimizer(target.Y, 0);
        OptCo = new Optimizer(target.Co, 1);
        OptCg = new Optimizer(target.Cg, 2);
        OptY.ColorOpt = OptCo.ColorOpt = OptCg.ColorOpt = this;
    }

    public double TotalRmsError(double curError, int curColor)
    {
        var eY = curColor == 0 ? curError : RmsY;
        var eCo = curColor == 1 ? curError : RmsCo;
        var eCg = curColor == 2 ? curError : RmsCg;
        return Math.Sqrt(eY * eY + eCo * eCo + eCg * eCg);
    }

    private (double y, double c) _lastDumpProgressError = (double.NaN, double.NaN);
    public void Dump(double error, Surface initial, Wavelet[] wavelets, bool anyImprovement, int color)
    {
        var eY = color == 0 ? error : RmsY;
        var eCo = color == 1 ? error : RmsCo;
        var eCg = color == 2 ? error : RmsCg;
        var eC = Math.Sqrt(eCo * eCo + eCg * eCg);

        if (double.IsNaN(_lastDumpProgressError.y) || (_lastDumpProgressError.y / eY > 1.001) || (eY <= _lastDumpProgressError.y && _lastDumpProgressError.c / eC > 1.002) || (anyImprovement && eY < _lastDumpProgressError.y))
        {
            _lastDumpProgressError = (eY, eC);
            // this looks weird in order to support dumping progress before a wavelet that's being tweaked has been fully committed to AllWavelets
            var img = initial.Clone();
            img.ApplyWavelets(wavelets, color);
            var surfY = color == 0 ? img : OptY.GetImage();
            var surfCo = color == 1 ? img : OptCo.GetImage();
            var surfCg = color == 2 ? img : OptCg.GetImage();
            using var bmp = YCoCg.Combine(surfY, surfCo, surfCg);
            bmp.Save($"dump-progress-{eY:000000.0000}-{eC:000000.0000}-{img.WaveletCount:0000}.png");
        }
    }

    public void LoadWavelets(string waveletsFile)
    {
        OptY.LoadWavelets(waveletsFile);
        SetWavelets(OptY.AllWavelets);
    }

    public void SetWavelets(IEnumerable<Wavelet> wavelets)
    {
        OptY.AllWavelets = wavelets.ToList();
        OptCo.AllWavelets = wavelets.ToList();
        OptCg.AllWavelets = wavelets.ToList();
    }

    public void OptimizeStep()
    {
        if (RmsY == 0)
            RmsY = OptY.TotalRmsError(OptY.AllWavelets);
        if (RmsCo == 0)
            RmsCo = OptCo.TotalRmsError(OptY.AllWavelets);
        if (RmsCg == 0)
            RmsCg = OptCg.TotalRmsError(OptY.AllWavelets); // the wavelets are the same across all three optimizers

        OptY.OptimizeStep(save: false);
        SetWavelets(OptY.AllWavelets);
        RmsY = OptY.TotalRmsError(OptY.AllWavelets);

        if (OptY.AllWavelets.Count <= 10 || (OptY.AllWavelets.Count < 50 && OptY.AllWavelets.Count % 2 == 0) || (OptY.AllWavelets.Count < 200 && OptY.AllWavelets.Count % 5 == 0) || (OptY.AllWavelets.Count % 20 == 0))
        {
            Console.WriteLine("Tweak color plane Co...");
            OptCo.TweakAllWavelets();
            SetWavelets(OptCo.AllWavelets);
            RmsCo = OptCo.TotalRmsError(OptCo.AllWavelets);

            Console.WriteLine("Tweak color plane Cg...");
            OptCg.TweakAllWavelets();
            SetWavelets(OptCg.AllWavelets);
            RmsCg = OptCg.TotalRmsError(OptCg.AllWavelets);
        }

        var newError = TotalRmsError(0, -1);
        File.AppendAllLines("wavelets.txt", new[] { $"RMS error at {OptY.AllWavelets.Count} wavelets: {newError}." });
        OptY.SaveWavelets($"wavelets-{OptY.AllWavelets.Count:0000}.txt");
        File.AppendAllLines($"wavelets-{OptY.AllWavelets.Count:0000}.txt", new[] { $"RMS FINAL error at {OptY.AllWavelets.Count} wavelets: {newError}" });
    }
}
