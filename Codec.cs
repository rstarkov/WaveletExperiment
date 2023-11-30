using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.ArithmeticCoding;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace WaveletExperiment;

class Codec
{
    public static void EncodeAll(Stream stream, Surface target, IEnumerable<Wavelet> wavelets, int tolerance = 0)
    {
        var s = new DoNotCloseStream(stream);
        s.WriteUInt32Optim((uint) target.Width);
        s.WriteUInt32Optim((uint) target.Height);
        s.WriteUInt32Optim((uint) target.Average);
        EncodeWaveletsCec(s, wavelets);
        if (tolerance < 2)
        {
            s.WriteByte(1);
            EncodeResidualsIncremental(s, target, wavelets, tolerance);
        }
        else if (tolerance < 100)
        {
            s.WriteByte(2);
            EncodeResidualsCec(s, target, wavelets, tolerance);
        }
        else
            s.WriteByte(0);
    }

    public static Surface DecodeAll(Stream stream)
    {
        var s = new DoNotCloseStream(stream);
        var width = (int) s.ReadUInt32Optim();
        var height = (int) s.ReadUInt32Optim();
        var background = (int) s.ReadUInt32Optim();
        var surface = new Surface(width, height, background);
        var wavelets = DecodeWaveletsCec(s);
        surface.ApplyWavelets(wavelets);
        var residualsType = s.ReadByte();
        if (residualsType != 0)
            throw new NotImplementedException();
        return surface;
    }

    public static void EncodeWaveletsTrivial(Stream stream, IEnumerable<Wavelet> wavelets)
    {
        stream.WriteUInt32Optim((uint) wavelets.Count());
        foreach (var wvl in wavelets)
            stream.WriteUInt32Optim((uint) wvl.X);
        foreach (var wvl in wavelets)
            stream.WriteUInt32Optim((uint) wvl.Y);
        foreach (var wvl in wavelets)
            stream.WriteUInt32Optim((uint) wvl.W);
        foreach (var wvl in wavelets)
            stream.WriteUInt32Optim((uint) wvl.H);
        foreach (var wvl in wavelets)
            stream.WriteUInt32Optim((uint) wvl.A);
        foreach (var wvl in wavelets)
            stream.WriteInt32Optim(wvl.Brightness);
    }

    public static void EncodeWaveletsSimple(Stream stream, IEnumerable<Wavelet> wavelets)
    {
        stream.WriteUInt32Optim((uint) wavelets.Count());
        var probsA = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(360, _ => 1));
        var probsB = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(521, _ => 1)); // -260...260
        stream.WriteUInt32Optim((uint) wavelets.Max(w => w.W));
        var probsW = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(wavelets.Max(w => w.W) + 1, _ => 1));
        stream.WriteUInt32Optim((uint) wavelets.Max(w => w.H));
        var probsH = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(wavelets.Max(w => w.H) + 1, _ => 1));

        stream.WriteUInt32Optim((uint) wavelets.Max(w => w.X));
        var probsX = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(wavelets.Max(w => w.X) + 1, _ => 1));
        stream.WriteUInt32Optim((uint) wavelets.Max(w => w.Y));
        var probsY = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(wavelets.Max(w => w.Y) + 1, _ => 1));

        var arith = new ArithmeticCodingWriter(stream, probsA);
        foreach (var wvl in wavelets)
        {
            arith.SetContext(probsX);
            arith.WriteSymbol(wvl.X);

            arith.SetContext(probsY);
            arith.WriteSymbol(wvl.Y);

            arith.SetContext(probsW);
            arith.WriteSymbol(wvl.W);
            probsW.IncrementSymbolFrequency(wvl.W);

            arith.SetContext(probsH);
            arith.WriteSymbol(wvl.H);
            probsH.IncrementSymbolFrequency(wvl.H);

            arith.SetContext(probsA);
            arith.WriteSymbol(wvl.A);

            arith.SetContext(probsB);
            arith.WriteSymbol(wvl.Brightness + 260);
            probsB.IncrementSymbolFrequency(wvl.Brightness + 260);
        }
        arith.Finalize(false);
    }

    public static void EncodeWaveletsCec(Stream stream, IEnumerable<Wavelet> wavelets)
    {
        stream.WriteUInt32Optim((uint) wavelets.Count());
        var probsA = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(360, _ => 1));
        var probsB = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(521, _ => 1)); // -260...260
        stream.WriteUInt32Optim((uint) wavelets.Max(w => w.W));
        var probsW = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(wavelets.Max(w => w.W) + 1, _ => 1));
        stream.WriteUInt32Optim((uint) wavelets.Max(w => w.H));
        var probsH = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(wavelets.Max(w => w.H) + 1, _ => 1));

        int maxWaveletsPerBlock = 2;
        stream.WriteUInt32Optim((uint) maxWaveletsPerBlock);
        var probsBlockType = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(2 + maxWaveletsPerBlock + 1, _ => 1)); // 1 symbol for subdivide, one for zero wavelets, N for n wavelets, 1 symbol for "too many wavelets" (possible if they happen to share the same exact X/Y, even if we don't expect this to happen often)

        var minX = wavelets.Min(w => w.X);
        var minY = wavelets.Min(w => w.Y);
        var maxXY = Math.Max(wavelets.Max(w => w.X), wavelets.Max(w => w.Y));
        stream.WriteUInt32Optim((uint) minX);
        stream.WriteUInt32Optim((uint) minY);
        stream.WriteUInt32Optim((uint) maxXY);

        var arith = new ArithmeticCodingWriter(stream, probsA);

        void outputBlockType(int type)
        {
            arith.SetContext(probsBlockType);
            arith.WriteSymbol(type);
            probsBlockType.IncrementSymbolFrequency(type);
        }

        void encodeBlock(int bx, int by, int bw, int bh)
        {
            var blockWavelets = wavelets.Where(w => w.X >= bx && w.X < bx + bw && w.Y >= by && w.Y < by + bh).ToList();
            // 0 = subdivide, 1 = zero wavelets in this block, 2 = 1 wavelet encoded, 3 = 2 wavelets encoded ... max+1 = max wavelets encoded followed by another count symbol

            if (blockWavelets.Count > maxWaveletsPerBlock && bw >= 2 && bh >= 2)
            {
                // Subdivide
                outputBlockType(0);
                var bw1 = bw / 2;
                var bw2 = bw - bw1;
                var bh1 = bh / 2;
                var bh2 = bh - bh1;
                encodeBlock(bx, by, bw1, bh1);
                encodeBlock(bx + bw1, by, bw2, bh1);
                encodeBlock(bx, by + bh1, bw1, bh2);
                encodeBlock(bx + bw1, by + bh1, bw2, bh2);
            }
            else
            {
                // Output all wavelets
                var probsX = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(bw, _ => 1));
                var probsY = bw == bh ? probsX : new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(bh, _ => 1));

                while (blockWavelets.Count > maxWaveletsPerBlock)
                {
                    outputWavelets(maxWaveletsPerBlock + 2, blockWavelets.Take(maxWaveletsPerBlock));
                    blockWavelets = blockWavelets.Skip(maxWaveletsPerBlock).ToList();
                }
                outputWavelets(blockWavelets.Count + 1, blockWavelets);

                void outputWavelets(int blockType, IEnumerable<Wavelet> wvls)
                {
                    outputBlockType(blockType);
                    foreach (var wvl in wvls)
                    {
                        arith.SetContext(probsX);
                        arith.WriteSymbol(wvl.X - bx);

                        arith.SetContext(probsY);
                        arith.WriteSymbol(wvl.Y - by);

                        arith.SetContext(probsW);
                        arith.WriteSymbol(wvl.W);
                        probsW.IncrementSymbolFrequency(wvl.W);

                        arith.SetContext(probsH);
                        arith.WriteSymbol(wvl.H);
                        probsH.IncrementSymbolFrequency(wvl.H);

                        arith.SetContext(probsA);
                        arith.WriteSymbol(wvl.A);

                        arith.SetContext(probsB);
                        arith.WriteSymbol(wvl.Brightness + 260);
                        probsB.IncrementSymbolFrequency(wvl.Brightness + 260);
                    }
                }
            }
        }

        encodeBlock(minX, minY, maxXY - minX + 1, maxXY - minY + 1);

        arith.Finalize(false);
    }

    public static IEnumerable<Wavelet> DecodeWaveletsCec(Stream stream)
    {
        var count = stream.ReadUInt32Optim();
        var probsA = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(360, _ => 1));
        var probsB = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(521, _ => 1)); // -260...260
        var maxW = (int) stream.ReadUInt32Optim();
        var probsW = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(maxW + 1, _ => 1));
        var maxH = (int) stream.ReadUInt32Optim();
        var probsH = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(maxH + 1, _ => 1));

        int maxWaveletsPerBlock = (int) stream.ReadUInt32Optim();
        var probsBlockType = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(2 + maxWaveletsPerBlock + 1, _ => 1)); // 1 symbol for subdivide, one for zero wavelets, N for n wavelets, 1 symbol for "too many wavelets" (possible if they happen to share the same exact X/Y, even if we don't expect this to happen often)

        int minX = (int) stream.ReadUInt32Optim();
        int minY = (int) stream.ReadUInt32Optim();
        int maxXY = (int) stream.ReadUInt32Optim();

        var arith = new ArithmeticCodingReader(stream, probsA);
        var wavelets = new List<Wavelet>();

        int readBlockType()
        {
            arith.SetContext(probsBlockType);
            var type = arith.ReadSymbol();
            probsBlockType.IncrementSymbolFrequency(type);
            return type;
        }

        void decodeBlock(int bx, int by, int bw, int bh)
        {
            var type = readBlockType();

            if (type == 0)
            {
                // Subdivide
                var bw1 = bw / 2;
                var bw2 = bw - bw1;
                var bh1 = bh / 2;
                var bh2 = bh - bh1;
                decodeBlock(bx, by, bw1, bh1);
                decodeBlock(bx + bw1, by, bw2, bh1);
                decodeBlock(bx, by + bh1, bw1, bh2);
                decodeBlock(bx + bw1, by + bh1, bw2, bh2);
            }
            else
            {
                // Read all wavelets
                var probsX = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(bw, _ => 1));
                var probsY = bw == bh ? probsX : new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(bh, _ => 1));

                while (type == maxWaveletsPerBlock + 2)
                {
                    readWavelets(maxWaveletsPerBlock);
                    type = readBlockType();
                }
                readWavelets(type - 1);

                void readWavelets(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var wvl = new Wavelet();
                        wavelets.Add(wvl);

                        arith.SetContext(probsX);
                        wvl.X = arith.ReadSymbol() + bx;

                        arith.SetContext(probsY);
                        wvl.Y = arith.ReadSymbol() + by;

                        arith.SetContext(probsW);
                        wvl.W = arith.ReadSymbol();
                        probsW.IncrementSymbolFrequency(wvl.W);

                        arith.SetContext(probsH);
                        wvl.H = arith.ReadSymbol();
                        probsH.IncrementSymbolFrequency(wvl.H);

                        arith.SetContext(probsA);
                        wvl.A = arith.ReadSymbol();

                        arith.SetContext(probsB);
                        wvl.Brightness = arith.ReadSymbol() - 260;
                        probsB.IncrementSymbolFrequency(wvl.Brightness + 260);
                    }
                }
            }
        }

        decodeBlock(minX, minY, maxXY - minX + 1, maxXY - minY + 1);
        arith.Finalize();

        return wavelets;
    }

    public static int[] ResidualsToSymbols(Surface target, Surface image, int tolerance = 0)
    {
        if (target.Width != image.Width || target.Height != image.Height)
            throw new ArgumentException();
        return target.Data.Zip(image.Data, (pixTarget, pixActual) =>
        {
            int symbol = (int) (Math.Round(pixActual).Clip(0, 255) - pixTarget); // [-255, 255]
            if (Math.Abs(symbol) <= tolerance)
                return 0;
            // transform so that the symbols code for 0 [0], 1 [1], -1 [2], 2 [3], -2 [4], 3 [5], -3 [6] etc in this order
            if (symbol > 0)
                return symbol * 2 - 1;
            else
                return -symbol * 2;
        }).ToArray();
    }

    public static void EncodeResidualsIncremental(Stream stream, Surface target, IEnumerable<Wavelet> wavelets, int tolerance = 0)
    {
        var image = new Surface(target.Width, target.Height, target.Average);
        image.ApplyWavelets(wavelets);
        EncodeResidualsIncremental(stream, target, image, tolerance);
    }

    public static void EncodeResidualsIncremental(Stream stream, Surface target, Surface image, int tolerance = 0)
    {
        stream.WriteUInt32Optim((uint) tolerance);
        var symbols = ResidualsToSymbols(target, image, tolerance);
        encodeResidualsIncremental(stream, symbols);
    }

    private static void encodeResidualsIncremental(Stream stream, int[] symbols)
    {
        // symbols range from -255 to 255, shifted by 255: [0, 510]
        var frequencies = new ArithmeticSymbolArrayContext(Ut.NewArray<uint>(511, _ => 1));
        const int exactFreqs = 40;
        for (int i = 0; i < exactFreqs; i++)
        {
            frequencies.SetSymbolFrequency(i, (uint) symbols.Count(s => s == i));
            stream.WriteUInt64Optim(frequencies.GetSymbolFrequency(i));
        }
        var arith = new ArithmeticCodingWriter(new DoNotCloseStream(stream), frequencies);
        foreach (var symbol in symbols)
        {
            arith.WriteSymbol(symbol);
            if (symbol >= exactFreqs)
                frequencies.IncrementSymbolFrequency(symbol);
        }
        arith.Finalize(false);
    }

    private static void writeUintsArith(Stream stream, IEnumerable<uint> values)
    {
        uint max = values.Max();
        stream.WriteUInt32Optim(max);
        var probs = Ut.NewArray<uint>((int) max + 1, _ => 1);
        var arith = new ArithmeticCodingWriter(new DoNotCloseStream(stream), probs);
        foreach (var val in values)
            arith.WriteSymbol(checked((int) val));
    }

    public static void EncodeResidualsExponential(Stream stream, Surface target, IEnumerable<Wavelet> wavelets, int tolerance = 0)
    {
        var image = new Surface(target.Width, target.Height, target.Average);
        image.ApplyWavelets(wavelets);
        EncodeResidualsExponential(stream, target, image, tolerance);
    }

    public static void EncodeResidualsExponential(Stream stream, Surface target, Surface image, int tolerance = 0)
    {
        stream.WriteUInt32Optim((uint) tolerance);
        var symbols = ResidualsToSymbols(target, image, tolerance);
        encodeResidualsExponential(stream, symbols);
    }

    private static void encodeResidualsExponential(Stream stream, int[] symbols)
    {
        var tweak1 = 5.0;
        var tweak2 = 10.0;
        byte[] best = encodeResidualsExponentialInner(tweak1, tweak2, symbols);
        var bestTweak1 = tweak1;
        var bestTweak2 = tweak2;
        var dir1 = 0.1;
        var dir2 = 0.1;
        int noImprovementCount = 0;
        while (noImprovementCount < 50)
        {
            tweak1 += dir1;
            tweak2 += dir2;
            var bytes = encodeResidualsExponentialInner(tweak1, tweak2, symbols);
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
        var bs = new BinaryStream(stream);
        bs.WriteDouble(bestTweak1);
        bs.WriteDouble(bestTweak2);
        for (int i = 0; i < 20; i++)
            bs.WriteUInt32Optim((uint) symbols.Count(s => s == i));
        stream.Write(best);
    }

    private static byte[] encodeResidualsExponentialInner(double tweak1, double tweak2, int[] symbols)
    {
        // symbols range from -255 to 255, shifted by 255: [0, 510]
        var frequencies = Enumerable.Range(0, 511).Select(x => (uint) (100000 * Math.Exp(-tweak1 * (x / 511.0 * tweak2)))).Select(x => x < 1 ? 1 : x).ToArray();
        for (int i = 0; i < 20; i++)
            frequencies[i] = (uint) symbols.Count(s => s == i);
        //var frequencies = Enumerable.Range(0, 511).Select(_ => 1UL).ToArray();
        using (var ms = new MemoryStream())
        {
            var arith = new ArithmeticCodingWriter(ms, frequencies);
            foreach (var symbol in symbols)
                arith.WriteSymbol(symbol);
            arith.Finalize(false);
            return ms.ToArray();
        }
    }

    public static void EncodeResidualsCec(Stream stream, Surface target, IEnumerable<Wavelet> wavelets, int tolerance = 0)
    {
        var image = new Surface(target.Width, target.Height, target.Average);
        image.ApplyWavelets(wavelets);
        EncodeResidualsCec(stream, target, image, tolerance);
    }

    public static void EncodeResidualsCec(Stream stream, Surface target, Surface image, int tolerance = 0)
    {
        var symbols = ResidualsToSymbols(target, image, tolerance);

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
        stream.WriteUInt32Optim((uint) tolerance);
        stream.WriteUInt32Optim((uint) bestThresh1);
        stream.WriteUInt32Optim((uint) bestThresh2);
        stream.Write(best, 0, best.Length);
    }

    private static byte[] encodeResidualsCecBlock(int[] symbols, int width, int height, int maxSubdivThresh, int typSubdivThresh)
    {
        var frequenciesPixels = new ArithmeticSymbolArrayContext(Ut.NewArray(511, _ => 1U));
        var frequenciesBlocks = new ArithmeticSymbolArrayContext(Ut.NewArray(5, _ => 1U));
        var ms = new MemoryStream();
        var arith = new ArithmeticCodingWriter(ms, frequenciesBlocks);

        void writeBlockTypeSymbol(int symbol)
        {
            arith.SetContext(frequenciesBlocks);
            arith.WriteSymbol(symbol);
            frequenciesBlocks.IncrementSymbolFrequency(symbol);
        }

        void writePixelSymbol(int symbol)
        {
            arith.SetContext(frequenciesPixels);
            arith.WriteSymbol(symbol);
            frequenciesPixels.IncrementSymbolFrequency(symbol);
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
        arith.Finalize(false);
        return ms.ToArray();
    }
}
