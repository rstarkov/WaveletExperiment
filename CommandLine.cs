using System;
using System.Drawing;
using System.IO;
using RT.CommandLine;

namespace WaveletExperiment;

[CommandLine]
abstract class CmdLine
{
    public abstract int Execute();
}

// rescale wavelets and save to wavelets file
// tweak for errors pass
// tweak for residuals pass - optionally with throwing away wavelets
// analyse wavelets folder for compression ratio

[CommandName("d", "decode")]
class CmdDecode : CmdLine
{
    [Option("-w", "--wavelet-limit")]
    [DocumentationRhoML("{h}Maximum number of wavelets to decode.{}\r\nIf specified, any residuals in the input file are ignored.")]
    public int? WaveletLimit = null;

    [Option("-s", "--scale")]
    [DocumentationRhoML("{h}Scaling factor for the output{}\r\nA ratio of two integers (e.g. 8/5) specifying the scaling factor for the decoded image. Residuals are ignored if specified.")]
    public string Scale = "1";

    [Option("--residuals-file")]
    [DocumentationRhoML("{h}Residuals visualisation dump file{}\r\nIf specified, dumps residuals found in the input file to a PNG file for visualisation purposes.")]
    public string ResidualsDumpFile = null;

    // residuals tolerance to simulate what it would be like?

    [Option("--wavelets-file")]
    [DocumentationRhoML("{h}Wavelets text dump file{}\r\nIf specified, dumps wavelets found in the input file to a text file.")]
    public string WaveletsDumpFile = null;

    [IsPositional, IsMandatory]
    [DocumentationRhoML("{h}File to decode{}")]
    public string InputFile = null;
    [IsPositional, IsMandatory]
    [DocumentationRhoML("{h}Decoded output{}\r\nOptional; PNG format.")]
    public string OutputFile = null;

    public override int Execute()
    {
        throw new NotImplementedException();
    }
}

[CommandName("e", "encode")]
class CmdEncode : CmdLine
{
    [Option("-t", "--tolerance")]
    [DocumentationRhoML("{h}Error tolerance for residuals{}\r\nAll residuals whose absolute value does not exceed this value are discarded (saved as 0). Use 0 (default) for a lossless encode and 100 for no residuals.")]
    public int ResidualsTolerance = 0;
    [Option("--residuals-file")]
    [DocumentationRhoML("{h}Residuals visualisation dump file{}\r\nIf specified, dumps residuals found in the input file to a PNG file for visualisation purposes.")]
    public string ResidualsDumpFile = null;

    // limit wavelets?
    // save decoded output right away? (ie the entirety of the decode command inside this one)

    [IsPositional, IsMandatory]
    public string OriginalFile = null;
    [IsPositional, IsMandatory]
    public string WaveletsFile = null;
    [IsPositional]
    public string OutputFile = null;

    public override int Execute()
    {
        throw new NotImplementedException();
    }
}

[CommandName("o", "optimize")]
class CmdOptimize : CmdLine
{
    [IsPositional, IsMandatory]
    public string OriginalFile = null;
    [IsPositional, IsMandatory]
    public string OutputPath = null;
    [Option("-r", "--resume")]
    public string WaveletsFile = null;
    [Option("-g", "--grayscale")]
    public bool Grayscale = false;

    public override int Execute()
    {
        OriginalFile = Path.GetFullPath(OriginalFile);
        if (WaveletsFile != null)
            WaveletsFile = Path.GetFullPath(WaveletsFile);
        Environment.CurrentDirectory = Path.GetFullPath(OutputPath);

        var target = YCoCg.Split(new Bitmap(OriginalFile));
        YCoCg.Combine(target.Y, target.Co, target.Cg).Save("target.png");

        if (Grayscale)
        {
            var opt = new Optimizer(target.Y, 0);
            if (WaveletsFile != null)
                opt.LoadWavelets(WaveletsFile);
            var start1 = DateTime.UtcNow;
            while (true)
            {
                var start2 = DateTime.UtcNow;
                opt.OptimizeStep();
                Console.WriteLine($"Step time: {(DateTime.UtcNow - start2).TotalSeconds:#,0.000} seconds. Total time: {(DateTime.UtcNow - start1).TotalHours:0.000} hours");
            }
        }
        else
        {
            var opt = new ColorOptimizer(target);
            if (WaveletsFile != null)
                opt.LoadWavelets(WaveletsFile);
            var start1 = DateTime.UtcNow;
            while (true)
            {
                var start2 = DateTime.UtcNow;
                opt.OptimizeStep();
                Console.WriteLine($"Step time: {(DateTime.UtcNow - start2).TotalSeconds:#,0.000} seconds. Total time: {(DateTime.UtcNow - start1).TotalHours:0.000} hours");
            }
        }
    }
}
