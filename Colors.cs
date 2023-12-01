using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using RT.Util.ExtensionMethods;

namespace WaveletExperiment;

static class YCoCg
{
    public static unsafe (Surface Y, Surface Co, Surface Cg) Split(Bitmap img)
    {
        using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImageUnscaled(img, 0, 0);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var Y = new Surface(bmp.Width, bmp.Height, 0);
        var Co = new Surface(bmp.Width, bmp.Height, 0);
        var Cg = new Surface(bmp.Width, bmp.Height, 0);
        for (int y = 0; y < bmp.Height; y++)
        {
            byte* end = ((byte*) bmpData.Scan0) + y * bmpData.Stride + 3 * bmp.Width;
            int x = 0;
            for (byte* data = ((byte*) bmpData.Scan0) + y * bmpData.Stride; data < end; x++, data += 3)
            {
                byte b = *data;
                byte g = *(data + 1);
                byte r = *(data + 2);
                Co[x, y] = r - b;
                var tmp = b + (int) Co[x, y] / 2;
                Cg[x, y] = g - tmp;
                Y[x, y] = tmp + (int) Cg[x, y] / 2;
            }
        }
        return (Y, Co, Cg);
    }

    public static unsafe Bitmap Combine(Surface Y, Surface Co, Surface Cg)
    {
        var bmp = new Bitmap(Y.Width, Y.Height, PixelFormat.Format24bppRgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        for (int y = 0; y < bmp.Height; y++)
        {
            byte* end = ((byte*) bmpData.Scan0) + y * bmpData.Stride + 3 * bmp.Width;
            int x = 0;
            for (byte* data = ((byte*) bmpData.Scan0) + y * bmpData.Stride; data < end; x++, data += 3)
            {
                var tmp = (int) Y[x, y].Clip(0, 255) - (int) Cg[x, y].Clip(-255, 255) / 2;
                *(data + 1) /*G*/ = (byte) (Cg[x, y].Clip(-255, 255) + tmp).Clip(0, 255);
                *data /*B*/ = (byte) (tmp - (int) Co[x, y].Clip(-255, 255) / 2).Clip(0, 255);
                *(data + 2) /*R*/ = (byte) (*data/*B*/ + Co[x, y].Clip(-255, 255)).Clip(0, 255);
            }
        }
        return bmp;
    }
}
