using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace WaveletExperiment
{
    class Codec
    {
        public static void EncodeAll(Stream stream, Surface target, IEnumerable<Wavelet> wavelets, int tolerance = 0)
        {
            stream.WriteUInt32Optim((uint) target.Width);
            stream.WriteUInt32Optim((uint) target.Height);
            EncodeWavelets(stream, wavelets);
            EncodeResiduals(stream, target, wavelets, tolerance);
        }

        public static void EncodeWavelets(Stream stream, IEnumerable<Wavelet> wavelets)
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

        public static void EncodeResiduals(Stream stream, Surface target, IEnumerable<Wavelet> wavelets, int tolerance = 0)
        {
            var residuals = new Surface(target.Width, target.Height);
            residuals.ApplyWavelets(wavelets);
            residuals.Merge(target, (ip, op) => Math.Round(ip).Clip(0, 255) - op);
            //residuals.Process(p => Math.Abs(p) <= 3 ? 0 : 255);
            //residuals.Save("residuals3.png");
            //return;
            var symbols = residuals.Data.Select(p =>
            {
                int symbol = (int) p; // [-255, 255]
                if (Math.Abs(symbol) <= tolerance)
                    return 0;
                // transform so that the symbols code for 0 [0], 1 [1], -1 [2], 2 [3], -2 [4], 3 [5], -3 [6] etc in this order
                if (symbol > 0)
                    return symbol * 2 - 1;
                else
                    return -symbol * 2;
            }).ToArray();

            var tweak1 = 5.0;
            var tweak2 = 10.0;
            byte[] best = encodeResiduals(tweak1, tweak2, symbols);
            var bestTweak1 = tweak1;
            var bestTweak2 = tweak2;
            var dir1 = 0.1;
            var dir2 = 0.1;
            int noImprovementCount = 0;
            while (noImprovementCount < 50)
            {
                tweak1 += dir1;
                tweak2 += dir2;
                var bytes = encodeResiduals(tweak1, tweak2, symbols);
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
            using (var bs = new BinaryStream(stream))
            {
                bs.WriteDouble(bestTweak1);
                bs.WriteDouble(bestTweak2);
                bs.WriteUInt32Optim((uint) tolerance);
                for (int i = 0; i < 20; i++)
                    bs.WriteUInt32Optim((uint) symbols.Count(s => s == i));
                stream.Write(best);
            }
        }

        private static byte[] encodeResiduals(double tweak1, double tweak2, int[] symbols)
        {
            // symbols range from -255 to 255, shifted by 255: [0, 510]
            var frequencies = Enumerable.Range(0, 511).Select(x => (ulong) (100000 * Math.Exp(-tweak1 * (x / 511.0 * tweak2)))).Select(x => x < 1 ? 1 : x).ToArray();
            for (int i = 0; i < 20; i++)
                frequencies[i] = (ulong) symbols.Count(s => s == i);
            //var frequencies = Enumerable.Range(0, 511).Select(_ => 1UL).ToArray();
            using (var ms = new MemoryStream())
            {
                var arith = new ArithmeticCodingWriter(ms, frequencies);
                foreach (var symbol in symbols)
                    arith.WriteSymbol(symbol);
                arith.Close(false);
                return ms.ToArray();
            }
        }

        public static void EncodeResidualsCec(string filename, Surface target, List<Wavelet> wavelets, int tolerance = 0)
        {
            var residuals = new Surface(target.Width, target.Height);
            residuals.ApplyWavelets(wavelets);
            residuals.Merge(target, (ip, op) => Math.Round(ip).Clip(0, 255) - op);
            //residuals.Process(p => Math.Abs(p) <= 3 ? 0 : 255);
            //residuals.Save("residuals3.png");
            //return;
            var symbols = residuals.Data.Select(p =>
            {
                int symbol = (int) p; // [-255, 255]
                if (Math.Abs(symbol) <= tolerance)
                    return 0;
                // transform so that the symbols code for 0 [0], 1 [1], -1 [2], 2 [3], -2 [4], 3 [5], -3 [6] etc in this order
                if (symbol > 0)
                    return symbol * 2 - 1;
                else
                    return -symbol * 2;
            }).ToArray();

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
            using (var file = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bs = new BinaryStream(file))
            {
                bs.WriteUInt32Optim((uint) tolerance);
                bs.WriteUInt32Optim((uint) bestThresh1);
                bs.WriteUInt32Optim((uint) bestThresh2);
                file.Write(best);
            }
        }

        private static byte[] encodeResidualsCecBlock(int[] symbols, int width, int height, int maxSubdivThresh, int typSubdivThresh)
        {
            ulong[] frequenciesPixels = RT.Util.Ut.NewArray(511, _ => 1UL);
            ulong[] frequenciesBlocks = RT.Util.Ut.NewArray(5, _ => 1UL);
            var ms = new MemoryStream();
            var arith = new ArithmeticCodingWriter(ms, frequenciesBlocks);

            void writeBlockTypeSymbol(int symbol)
            {
                arith.TweakProbabilities(frequenciesBlocks);
                arith.WriteSymbol(symbol);
                frequenciesBlocks[symbol]++;
            }

            void writePixelSymbol(int symbol)
            {
                arith.TweakProbabilities(frequenciesPixels);
                arith.WriteSymbol(symbol);
                frequenciesPixels[symbol]++;
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
            arith.Close(false);
            return ms.ToArray();
        }
    }
}
