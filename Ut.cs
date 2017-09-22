using System;
using D = System.Drawing;

namespace TankIconMaker
{
    static partial class Ut
    {
        /// <summary>Returns a new blank (transparent) GDI bitmap of the specified size.</summary>
        /// <param name="draw">Optionally a method to draw into the returned image.</param>
        public static BitmapGdi NewBitmapGdi(int width, int height, Action<D.Graphics> draw)
        {
            var result = new BitmapGdi(width, height);
            if (draw != null)
                using (var g = D.Graphics.FromImage(result.Bitmap))
                    draw(g);
            return result;
        }

        public static BitmapRam ToBitmapRam(this D.Image src)
        {
            return ToBitmapGdi(src).ToBitmapRam();
        }

        public static BitmapGdi ToBitmapGdi(this D.Image src)
        {
            return Ut.NewBitmapGdi(src.Width, src.Height, dc => { dc.DrawImageUnscaled(src, 0, 0); });
        }

        /// <summary>A shorthand for drawing an image into a GDI image at coordinate 0,0.</summary>
        public static BitmapGdi DrawImage(this BitmapGdi target, BitmapGdi source)
        {
            using (var g = D.Graphics.FromImage(target.Bitmap))
                g.DrawImageUnscaled(source.Bitmap, 0, 0);
            return target;
        }

        public static unsafe void BlendImages(byte* imgLeft, int strideLeft, byte* imgRight, int strideRight, byte* imgResult, int strideResult, int width, int height, double rightAmount)
        {
            var leftAmount = 1 - rightAmount;
            for (int y = 0; y < height; y++)
            {
                byte* ptrLeft = imgLeft + y * strideLeft;
                byte* ptrRight = imgRight + y * strideLeft;
                byte* ptrResult = imgResult + y * strideLeft;
                byte* endResult = ptrResult + width * 4;
                for (; ptrResult < endResult; ptrLeft += 4, ptrRight += 4, ptrResult += 4)
                {
                    double rightRatio = blendRightRatio(*(ptrLeft + 3), *(ptrRight + 3), rightAmount);
                    double leftRatio = 1 - rightRatio;

                    *(ptrResult + 0) = (byte) (*(ptrLeft + 0) * leftRatio + *(ptrRight + 0) * rightRatio);
                    *(ptrResult + 1) = (byte) (*(ptrLeft + 1) * leftRatio + *(ptrRight + 1) * rightRatio);
                    *(ptrResult + 2) = (byte) (*(ptrLeft + 2) * leftRatio + *(ptrRight + 2) * rightRatio);
                    *(ptrResult + 3) = (byte) (*(ptrLeft + 3) * leftAmount + *(ptrRight + 3) * rightAmount);
                }
            }
        }

        /// <summary>Blends two colors. If the amount is 0, only the left color is present. If it's 1, only the right is present.</summary>
        public static D.Color BlendColors(D.Color left, D.Color right, double rightAmount)
        {
            double rightRatio = blendRightRatio(left.A, right.A, rightAmount);
            return D.Color.FromArgb(
                alpha: (byte) Math.Round(left.A * (1 - rightAmount) + right.A * rightAmount),
                red: (byte) Math.Round(left.R * (1 - rightRatio) + right.R * rightRatio),
                green: (byte) Math.Round(left.G * (1 - rightRatio) + right.G * rightRatio),
                blue: (byte) Math.Round(left.B * (1 - rightRatio) + right.B * rightRatio));
        }

        /// <summary>Calculates the blend ratio for blending colors with arbitrary alpha values.</summary>
        private static double blendRightRatio(byte leftAlpha, byte rightAlpha, double rightAmount)
        {
            if (leftAlpha < rightAlpha)
                return 1.0 - 2.0 * leftAlpha / (double) (leftAlpha + rightAlpha) * (1.0 - rightAmount);
            else if (rightAlpha < leftAlpha)
                return 2.0 * rightAlpha / (double) (leftAlpha + rightAlpha) * rightAmount;
            else
                return rightAmount;
        }

        /// <summary>Returns the same color but with the specified value for the alpha channel.</summary>
        public static D.Color WithAlpha(this D.Color color, int alpha)
        {
            if (alpha < 0 || alpha > 255) throw new ArgumentOutOfRangeException("alpha");
            return D.Color.FromArgb((byte) alpha, color.R, color.G, color.B);
        }

        public static int ModPositive(int value, int modulus)
        {
            int result = value % modulus;
            return result >= 0 ? result : (result + modulus);
        }
    }

    /// <summary>Adapted from Paint.NET and thus exactly compatible in the RGB/HSV conversion (apart from hue 360, which must be 0 instead)</summary>
    struct ColorHSV
    {
        /// <summary>Hue, 0..359</summary>
        public int Hue { get; private set; }
        /// <summary>Saturation, 0..100</summary>
        public int Saturation { get; private set; }
        /// <summary>Value, 0..100</summary>
        public int Value { get; private set; }
        /// <summary>Alpha, range 0..255</summary>
        public int Alpha { get; private set; }

        private ColorHSV(int hue, int saturation, int value, int alpha)
            : this()
        {
            if (hue < 0 || hue > 359) throw new ArgumentException("hue");
            if (saturation < 0 || saturation > 100) throw new ArgumentException("saturation");
            if (value < 0 || value > 100) throw new ArgumentException("value");
            if (alpha < 0 || alpha > 255) throw new ArgumentException("alpha");
            Hue = hue;
            Saturation = saturation;
            Value = value;
            Alpha = alpha;
        }

        public static ColorHSV FromHSV(int hue, int saturation, int value, int alpha = 255)
        {
            return new ColorHSV(hue, saturation, value, alpha);
        }

        public D.Color ToColorGdi()
        {
            double h;
            double s;
            double v;

            double r = 0;
            double g = 0;
            double b = 0;

            // Scale Hue to be between 0 and 360. Saturation
            // and value scale to be between 0 and 1.
            h = (double) Hue % 360;
            s = (double) Saturation / 100;
            v = (double) Value / 100;

            if (s == 0)
            {
                // If s is 0, all colors are the same.
                // This is some flavor of gray.
                r = v;
                g = v;
                b = v;
            }
            else
            {
                double p;
                double q;
                double t;

                double fractionalSector;
                int sectorNumber;
                double sectorPos;

                // The color wheel consists of 6 sectors.
                // Figure out which sector you're in.
                sectorPos = h / 60;
                sectorNumber = (int) (Math.Floor(sectorPos));

                // get the fractional part of the sector.
                // That is, how many degrees into the sector
                // are you?
                fractionalSector = sectorPos - sectorNumber;

                // Calculate values for the three axes
                // of the color. 
                p = v * (1 - s);
                q = v * (1 - (s * fractionalSector));
                t = v * (1 - (s * (1 - fractionalSector)));

                // Assign the fractional colors to r, g, and b
                // based on the sector the angle is in.
                switch (sectorNumber)
                {
                    case 0: r = v; g = t; b = p; break;
                    case 1: r = q; g = v; b = p; break;
                    case 2: r = p; g = v; b = t; break;
                    case 3: r = p; g = q; b = v; break;
                    case 4: r = t; g = p; b = v; break;
                    case 5: r = v; g = p; b = q; break;
                }
            }
            return D.Color.FromArgb((byte) Alpha, (byte) (r * 255), (byte) (g * 255), (byte) (b * 255));
        }

        public static ColorHSV FromColor(D.Color color)
        {
            // In this function, R, G, and B values must be scaled 
            // to be between 0 and 1.
            // HsvColor.Hue will be a value between 0 and 360, and 
            // HsvColor.Saturation and value are between 0 and 1.

            double min;
            double max;
            double delta;

            double r = (double) color.R / 255;
            double g = (double) color.G / 255;
            double b = (double) color.B / 255;

            double h;
            double s;
            double v;

            min = Math.Min(Math.Min(r, g), b);
            max = Math.Max(Math.Max(r, g), b);
            v = max;
            delta = max - min;

            if (max == 0 || delta == 0)
            {
                // R, G, and B must be 0, or all the same.
                // In this case, S is 0, and H is undefined.
                // Using H = 0 is as good as any...
                s = 0;
                h = 0;
            }
            else
            {
                s = delta / max;
                if (r == max)
                {
                    // Between Yellow and Magenta
                    h = (g - b) / delta;
                }
                else if (g == max)
                {
                    // Between Cyan and Yellow
                    h = 2 + (b - r) / delta;
                }
                else
                {
                    // Between Magenta and Cyan
                    h = 4 + (r - g) / delta;
                }

            }
            // Scale h to be between 0 and 360. 
            // This may require adding 360, if the value
            // is negative.
            h *= 60;

            if (h < 0)
            {
                h += 360;
            }

            // Scale to the requirements of this 
            // application. All values are between 0 and 255.
            return FromHSV((int) h, (int) (s * 100), (int) (v * 100), color.A);
        }

        public ColorHSV ScaleValue(double scale)
        {
            return FromHSV(Hue, Saturation, Math.Max(0, Math.Min(100, (int) Math.Round(Value * scale))), Alpha);
        }

        public ColorHSV WithAlpha(int alpha)
        {
            return FromHSV(Hue, Saturation, Value, Alpha);
        }
    }

    enum BlurEdgeMode
    {
        Transparent,
        Same,
        Mirror,
        Wrap,
    }

    class GaussianBlur
    {
        private int _radius;
        private int[] _kernel;
        private int _kernelSum;

        public double Radius { get; private set; }

        public GaussianBlur(double radius)
        {
            Radius = radius;
            _radius = (int) Math.Ceiling(radius);

            // Compute the kernel by sampling the gaussian
            int len = _radius * 2 + 1;
            double[] kernel = new double[len];
            double sigma = radius / 3;
            double sigma22 = 2 * sigma * sigma;
            double sigmaPi2 = 2 * Math.PI * sigma;
            double sqrtSigmaPi2 = (double) Math.Sqrt(sigmaPi2);
            double radius2 = radius * radius;
            double total = 0;
            int index = 0;
            for (int x = -_radius; x <= _radius; x++)
            {
                double distance = x * x;
                if (distance > radius2)
                    kernel[index] = 0;
                else
                    kernel[index] = Math.Exp(-distance / sigma22) / sqrtSigmaPi2;
                total += kernel[index];
                index++;
            }

            // Convert to integers
            _kernel = new int[len];
            _kernelSum = 0;
            double scale = 2147483647.0 / (255 * total * len); // scale so that the integer total can never overflow
            scale /= 5; // there will be rounding errors; make sure we don’t overflow even then
            for (int i = 0; i < len; i++)
            {
                _kernel[i] = (int) (kernel[i] * scale);
                _kernelSum += _kernel[i];
            }
        }

        internal unsafe void Horizontal(BitmapBase src, BitmapBase dest, BlurEdgeMode edgeMode)
        {
            for (int y = 0; y < src.Height; y++)
            {
                byte* rowSource = src.Data + y * src.Stride;
                byte* rowResult = dest.Data + y * dest.Stride;
                for (int x = 0; x < src.Width; x++)
                {
                    int rSum = 0, gSum = 0, bSum = 0, aSum = 0;
                    for (int k = 0, xSrc = x - _kernel.Length / 2; k < _kernel.Length; k++, xSrc++)
                    {
                        int xRead = xSrc;
                        if (xRead < 0 || xRead >= src.Width)
                            switch (edgeMode)
                            {
                                case BlurEdgeMode.Transparent:
                                    continue;
                                case BlurEdgeMode.Same:
                                    xRead = xRead < 0 ? 0 : src.Width - 1;
                                    break;
                                case BlurEdgeMode.Wrap:
                                    xRead = Ut.ModPositive(xRead, src.Width);
                                    break;
                                case BlurEdgeMode.Mirror:
                                    if (xRead < 0)
                                        xRead = -xRead - 1;
                                    xRead = xRead % (2 * src.Width);
                                    if (xRead >= src.Width)
                                        xRead = 2 * src.Width - xRead - 1;
                                    break;
                            }
                        xRead <<= 2; // * 4
                        bSum += _kernel[k] * rowSource[xRead + 0];
                        gSum += _kernel[k] * rowSource[xRead + 1];
                        rSum += _kernel[k] * rowSource[xRead + 2];
                        aSum += _kernel[k] * rowSource[xRead + 3];
                    }

                    int xWrite = x << 2; // * 4
                    rowResult[xWrite + 0] = (byte) (bSum / _kernelSum);
                    rowResult[xWrite + 1] = (byte) (gSum / _kernelSum);
                    rowResult[xWrite + 2] = (byte) (rSum / _kernelSum);
                    rowResult[xWrite + 3] = (byte) (aSum / _kernelSum);
                }
            }
        }

        internal unsafe void Vertical(BitmapBase src, BitmapBase dest, BlurEdgeMode edgeMode)
        {
            for (int x = 0; x < src.Width; x++)
            {
                byte* colSource = src.Data + x * 4;
                byte* colResult = dest.Data + x * 4;
                for (int y = 0; y < src.Height; y++)
                {
                    int rSum = 0, gSum = 0, bSum = 0, aSum = 0;
                    for (int k = 0, ySrc = y - _kernel.Length / 2; k < _kernel.Length; k++, ySrc++)
                    {
                        int yRead = ySrc;
                        if (yRead < 0 || yRead >= src.Height)
                            switch (edgeMode)
                            {
                                case BlurEdgeMode.Transparent:
                                    continue;
                                case BlurEdgeMode.Same:
                                    yRead = yRead < 0 ? 0 : src.Height - 1;
                                    break;
                                case BlurEdgeMode.Wrap:
                                    yRead = Ut.ModPositive(yRead, src.Height);
                                    break;
                                case BlurEdgeMode.Mirror:
                                    if (yRead < 0)
                                        yRead = -yRead - 1;
                                    yRead = yRead % (2 * src.Height);
                                    if (yRead >= src.Height)
                                        yRead = 2 * src.Height - yRead - 1;
                                    break;
                            }
                        yRead *= src.Stride;
                        bSum += _kernel[k] * colSource[yRead + 0];
                        gSum += _kernel[k] * colSource[yRead + 1];
                        rSum += _kernel[k] * colSource[yRead + 2];
                        aSum += _kernel[k] * colSource[yRead + 3];
                    }

                    int yWrite = y * dest.Stride;
                    colResult[yWrite + 0] = (byte) (bSum / _kernelSum);
                    colResult[yWrite + 1] = (byte) (gSum / _kernelSum);
                    colResult[yWrite + 2] = (byte) (rSum / _kernelSum);
                    colResult[yWrite + 3] = (byte) (aSum / _kernelSum);
                }
            }
        }
    }

    static partial class Ut
    {
        /// <summary>Copies <paramref name="len"/> bytes from one location to another. Works fastest if <paramref name="len"/> is divisible by 16.</summary>
        public static unsafe void MemSet(byte* dest, byte value, int len)
        {
            ushort ushort_ = (ushort) (value | (value << 8));
            uint uint_ = (uint) (ushort_ | (ushort_ << 16));
            ulong ulong_ = uint_ | ((ulong) uint_ << 32);
            if (len >= 16)
            {
                do
                {
                    *(ulong*) dest = ulong_;
                    *(ulong*) (dest + 8) = ulong_;
                    dest += 16;
                }
                while ((len -= 16) >= 16);
            }
            if (len > 0)
            {
                if ((len & 8) != 0)
                {
                    *(ulong*) dest = ulong_;
                    dest += 8;
                }
                if ((len & 4) != 0)
                {
                    *(uint*) dest = uint_;
                    dest += 4;
                }
                if ((len & 2) != 0)
                {
                    *(ushort*) dest = ushort_;
                    dest += 2;
                }
                if ((len & 1) != 0)
                    *dest = value;
            }
        }

        /// <summary>Copies <paramref name="len"/> bytes from one location to another. Works fastest if <paramref name="len"/> is divisible by 16.</summary>
        public static unsafe void MemCpy(byte* dest, byte* src, int len)
        {
            if (len >= 16)
            {
                do
                {
                    *(long*) dest = *(long*) src;
                    *(long*) (dest + 8) = *(long*) (src + 8);
                    dest += 16;
                    src += 16;
                }
                while ((len -= 16) >= 16);
            }
            if (len > 0)
            {
                if ((len & 8) != 0)
                {
                    *(long*) dest = *(long*) src;
                    dest += 8;
                    src += 8;
                }
                if ((len & 4) != 0)
                {
                    *(int*) dest = *(int*) src;
                    dest += 4;
                    src += 4;
                }
                if ((len & 2) != 0)
                {
                    *(short*) dest = *(short*) src;
                    dest += 2;
                    src += 2;
                }
                if ((len & 1) != 0)
                    *dest = *src;
            }
        }

        /// <summary>Copies <paramref name="len"/> bytes from one location to another. Works fastest if <paramref name="len"/> is divisible by 16.</summary>
        public static unsafe void MemCpy(byte[] dest, byte* src, int len)
        {
            if (len > dest.Length)
                throw new ArgumentOutOfRangeException("len");
            fixed (byte* destPtr = dest)
                MemCpy(destPtr, src, len);
        }

        /// <summary>Copies <paramref name="len"/> bytes from one location to another. Works fastest if <paramref name="len"/> is divisible by 16.</summary>
        public static unsafe void MemCpy(byte* dest, byte[] src, int len)
        {
            if (len > src.Length)
                throw new ArgumentOutOfRangeException("len");
            fixed (byte* srcPtr = src)
                MemCpy(dest, srcPtr, len);
        }
    }

    /// <summary>A better Int32Rect, expressly designed to represent pixel areas - hence the left/right/top/bottom/width/height are always "inclusive".</summary>
    struct PixelRect
    {
        private int _left, _width, _top, _height;
        /// <summary>The leftmost pixel included in the rect.</summary>
        public int Left { get { return _left; } }
        /// <summary>The topmost pixel included in the rect.</summary>
        public int Top { get { return _top; } }
        /// <summary>The rightmost pixel included in the rect.</summary>
        public int Right { get { return _left + _width - 1; } }
        /// <summary>The bottommost pixel included in the rect.</summary>
        public int Bottom { get { return _top + _height - 1; } }
        /// <summary>The total number of pixels, horizontally, included in the rect.</summary>
        public int Width { get { return _width; } }
        /// <summary>The total number of pixels, vertically, included in the rect.</summary>
        public int Height { get { return _height; } }
        /// <summary>The X coordinate of the center pixel. If the number of pixels in the rect is even, returns the pixel to the right of center.</summary>
        public int CenterHorz { get { return _left + _width / 2; } }
        /// <summary>The Y coordinate of the center pixel. If the number of pixels in the rect is even, returns the pixel to the bottom of center.</summary>
        public int CenterVert { get { return _top + _height / 2; } }
        /// <summary>The X coordinate of the center pixel. If the number of pixels in the rect is even, returns a non-integer value.</summary>
        public double CenterHorzD { get { return _left + _width / 2.0; } }
        /// <summary>The Y coordinate of the center pixel. If the number of pixels in the rect is even, returns a non-integer value.</summary>
        public double CenterVertD { get { return _top + _height / 2.0; } }

        public static PixelRect FromBounds(int left, int top, int right, int bottom)
        {
            return new PixelRect { _left = left, _top = top, _width = right - left + 1, _height = bottom - top + 1 };
        }
        public static PixelRect FromMixed(int left, int top, int width, int height)
        {
            return new PixelRect { _left = left, _top = top, _width = width, _height = height };
        }
        public static PixelRect FromLeftRight(int left, int right) { return FromBounds(left, 0, right, 0); }
        public static PixelRect FromTopBottom(int top, int bottom) { return FromBounds(0, top, 0, bottom); }
        public PixelRect WithLeftRight(int left, int right) { return FromBounds(left, Top, right, Bottom); }
        public PixelRect WithLeftRight(PixelRect width) { return FromBounds(width.Left, Top, width.Right, Bottom); }
        public PixelRect WithTopBottom(int top, int bottom) { return FromBounds(Left, top, Right, bottom); }
        public PixelRect WithTopBottom(PixelRect height) { return FromBounds(Left, height.Top, Right, height.Bottom); }
        public PixelRect Shifted(int deltaX, int deltaY) { return FromMixed(Left + deltaX, Top + deltaY, Width, Height); }
    }
}
