using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using RT.Util;
using RT.Util.CommandLine;
using RT.Util.Streams;

namespace WaveletExperiment
{
    [CommandLine]
    abstract class CmdLine
    {
    }

    // rescale wavelets and save to wavelets file
    // tweak for errors pass
    // tweak for residuals pass - optionally with throwing away wavelets
    // analyse wavelets folder for compression ratio

    [CommandName("d", "decode")]
    class CmdDecode : CmdLine
    {
        [Option("-w", "--wavelet-limit")]
        [DocumentationRhoML("{h}Maximum number of wavelets to decode.{}{nl}{}If specified, any residuals in the input file are ignored.")]
        public int? WaveletLimit = null;

        [Option("-s", "--scale")]
        [DocumentationRhoML("{h}Scaling factor for the output{}{nl}{}A ratio of two integers (e.g. 8/5) specifying the scaling factor for the decoded image. Residuals are ignored if specified.")]
        public string Scale = "1";

        [Option("--residuals-file")]
        [DocumentationRhoML("{h}Residuals visualisation dump file{}{nl}{}If specified, dumps residuals found in the input file to a PNG file for visualisation purposes.")]
        public string ResidualsDumpFile = null;

        // residuals tolerance to simulate what it would be like?

        [Option("--wavelets-file")]
        [DocumentationRhoML("{h}Wavelets text dump file{}{nl}{}If specified, dumps wavelets found in the input file to a text file.")]
        public string WaveletsDumpFile = null;

        [IsPositional, IsMandatory]
        [DocumentationRhoML("{h}File to decode{}")]
        public string InputFile = null;
        [IsPositional, IsMandatory]
        [DocumentationRhoML("{h}Decoded output{}{nl}{}Optional; PNG format.")]
        public string OutputFile = null;
    }

    [CommandName("e", "encode")]
    class CmdEncode : CmdLine
    {
        [Option("-t", "--tolerance")]
        [DocumentationRhoML("{h}Error tolerance for residuals{}{nl}{}All residuals whose absolute value does not exceed this value are discarded (saved as 0). Use 0 (default) for a lossless encode.")]
        public int ResidualsTolerance = 0;
        [Option("--residuals-file")]
        [DocumentationRhoML("{h}Residuals visualisation dump file{}{nl}{}If specified, dumps residuals found in the input file to a PNG file for visualisation purposes.")]
        public string ResidualsDumpFile = null;

        // limit wavelets?
        // save decoded output right away? (ie the entirety of the decode command inside this one)

        [IsPositional, IsMandatory]
        public string OriginalFile = null;
        [IsPositional, IsMandatory]
        public string WaveletsFile = null;
        [IsPositional]
        public string OutputFile = null;

    }

    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--post-build-check")
                return Ut.RunPostBuildChecks(args[1], Assembly.GetExecutingAssembly());

            var target = new Surface(new Bitmap(@"P:\WaveletExperiment\lena3.png"));
            target.Save("target.png");
            Rnd.Reset(12346);

            var opt = new Optimizer(target);
            var start1 = DateTime.UtcNow;
            while (true)
            {
                var start2 = DateTime.UtcNow;
                opt.OptimizeStep();
                Console.WriteLine($"Step time: {(DateTime.UtcNow - start2).TotalSeconds:#,0.000}s. Total time: {(DateTime.UtcNow - start1).TotalSeconds:#,0.000}s");
            }
        }

        private static void PostBuildCheck(IPostBuildReporter rep)
        {
            CommandLineParser.PostBuildStep<CmdLine>(rep, null);
        }

        private static void ResultsAnalysis(Surface target, string path, int from, int increment = 1)
        {
            var files = new DirectoryInfo(path).GetFiles("wavelets-*.txt").OrderBy(f => f.Name);
            var results = new List<string>();
            foreach (var f in files)
            {
                var opt = new Optimizer(target);
                opt.LoadWavelets(f.FullName);
                if (opt.AllWavelets.Count < from)
                    continue;
                from += increment;
                var start = DateTime.UtcNow;
                var ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 0);
                var length0 = ms.Length;
                ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 1);
                var length1 = ms.Length;
                ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 2);
                var length2 = ms.Length;
                ms = new MemoryStream();
                Codec.EncodeAll(ms, target, opt.AllWavelets, 3);
                var length3 = ms.Length;

                ms = new MemoryStream();
                Codec.EncodeWaveletsCec(new DoNotCloseStream(ms), opt.AllWavelets);
                var lengthW = ms.Length;
                Console.WriteLine($"Encode total: {(DateTime.UtcNow - start).TotalSeconds}");

                File.AppendAllLines(Path.Combine(path, "wavelets-zanalysis.txt"), new[] { $"At {opt.AllWavelets.Count} wavelets, RMS error = {Optimizer.TotalRmsError(opt.AllWavelets, new Surface(target.Width, target.Height), target)}, lossless = {length0:#,0} bytes, lossy 1 = {length1:#,0} bytes, lossy 2 = {length2:#,0} bytes, lossy 3 = {length3:#,0} bytes, wavelets only = {lengthW:#,0} bytes" });
            }
        }
    }
}
