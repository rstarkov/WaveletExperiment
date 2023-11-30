using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using RT.CommandLine;
using RT.PostBuild;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace WaveletExperiment;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--post-build-check")
            return PostBuildChecker.RunPostBuildChecks(args[1], Assembly.GetExecutingAssembly());
        Rnd.Reset(12346);
        var cmdline = CommandLineParser.ParseOrWriteUsageToConsole<CmdLine>(args);
        if (cmdline == null)
            return -1;
        return cmdline.Execute();
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
