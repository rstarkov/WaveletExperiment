using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using RT.Util.ExtensionMethods;

namespace WaveletExperiment;

class Surface
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double[] Data { get; private set; }
    public int WaveletCount { get; private set; } = 0;
    public int Average => (int) Math.Round(Data.Average()); // rounded to whole numbers as that's what we want to save into the stream, compactly, and we need easy access to this value throughout the code

    public Surface(int width, int height, double initial)
    {
        Width = width;
        Height = height;
        Data = new double[Width * Height];
        if (initial != 0)
            Array.Fill(Data, initial);
    }

    public unsafe Surface(Bitmap img)
    {
        Width = img.Width;
        Height = img.Height;
        Data = new double[Width * Height];
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImageUnscaled(img, 0, 0);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        for (int y = 0; y < Height; y++)
        {
            byte* end = ((byte*) bmpData.Scan0) + y * bmpData.Stride + 3 * Width;
            int x = 0;
            for (byte* data = ((byte*) bmpData.Scan0) + y * bmpData.Stride; data < end; x++, data += 3)
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
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
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

    public Surface Clone()
    {
        return new Surface(Width, Height, 0) { Data = Data.ToArray(), WaveletCount = WaveletCount };
    }

    public void CopyTo(Surface target)
    {
        if (target.Width != Width || target.Height != Height)
            throw new ArgumentException("Target image must have the same width and height", nameof(target));
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
