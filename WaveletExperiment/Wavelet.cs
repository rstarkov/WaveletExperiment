using System;
using System.Linq;
using RT.Util.ExtensionMethods;

namespace WaveletExperiment;

class Wavelet
{
    public int Brightness;
    public int Co, Cg;
    public int X, Y, W, H, A;
    public int MinX, MinY, MaxX, MaxY;

    private bool _precalculated = false;
    private double mXX, mXY, mYX, mYY, mXO, mYO;

    public override string ToString() { return $"X={X}; Y={Y}; W={W}; H={H}; A={A}; Y={Brightness}; Co={Co}; Cg={Cg}"; }

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
        if (parts.Length > 6)
        {
            Co = parts[6];
            Cg = parts[7];
        }
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
        return Math.Exp(-lengthSquared * 6.238324625039); // square of the length of (tx, ty), conveniently cancelling out the sqrt
        // 6.23... = ln 512, ie the point at which the value becomes less than 0.5 when scaled by 256, ie would round to 0
    }

    public Wavelet Clone()
    {
        return new Wavelet { X = X, Y = Y, W = W, H = H, A = A, Brightness = Brightness, Co = Co, Cg = Cg };
    }

    public void ApplyVector(int[] vector, int offset, bool negate, int color)
    {
        int mul = negate ? -1 : 1;
        if (color == 0)
        {
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
        else if (color == 1)
            Co = (Co + vector[offset + 0] * mul).Clip(-260, 260);
        else if (color == 2)
            Cg = (Cg + vector[offset + 0] * mul).Clip(-260, 260);
        else
            throw new Exception();
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
