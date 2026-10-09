using System.Globalization;
using Watermarker.Core;

return Cli.Run(args);

internal static class Cli
{
    private const int Ok = 0, UsageError = 1, Failure = 2;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
        {
            PrintHelp(null);
            return args.Length == 0 ? UsageError : Ok;
        }

        string? input = null, output = null, text = null, libreOffice = null, ffmpeg = null;
        double angle = 45, size = 60;
        double? opacity = null;
        string color = "#808080", font = "Arial";
        bool tile = false, pptxSlides = false, toPdf = false, listFormats = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"Missing value after {arg}.");

                switch (arg)
                {
                    case "-t" or "--text": text = Next().Replace("\\n", "\n"); break; // "\n" = new line
                    case "-o" or "--output": output = Next(); break;
                    case "-a" or "--angle": angle = ParseNumber(Next(), arg); break;
                    case "-s" or "--size": size = ParseNumber(Next(), arg); break;
                    case "-c" or "--color": color = Next(); break;
                    case "--opacity": opacity = ParseNumber(Next(), arg); break;
                    case "-f" or "--font": font = Next(); break;
                    case "--tile": tile = true; break;
                    case "--pptx-slides": pptxSlides = true; break;
                    case "--to-pdf": toPdf = true; break;
                    case "--libreoffice": libreOffice = Next(); break;
                    case "--ffmpeg": ffmpeg = Next(); break;
                    case "--formats": listFormats = true; break;
                    default:
                        if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
                        if (input is not null) throw new ArgumentException($"More than one file given: '{input}' and '{arg}'.");
                        input = arg;
                        break;
                }
            }

            if (!listFormats)
            {
                if (input is null) throw new ArgumentException("Specify the file to process.");
                if (text is null) throw new ArgumentException("Specify the text with --text.");
            }
            if (opacity is < 0 or > 1) throw new ArgumentException("--opacity must be between 0 and 1.");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Use --help for usage.");
            return UsageError;
        }

        var engine = new WatermarkEngine(new EngineSettings { LibreOfficePath = libreOffice, FfmpegPath = ffmpeg });

        if (listFormats)
        {
            PrintFormats(engine);
            return Ok;
        }

        try
        {
            var rgba = RgbaColor.Parse(color);
            if (opacity is { } op) rgba = rgba.WithOpacity(op);
            else if (!HasExplicitAlpha(color)) rgba = rgba.WithOpacity(0.35); // default leggibile

            var options = new WatermarkOptions
            {
                Text = TextTemplate.Expand(text!, input, Environment.UserName),
                AngleDegrees = angle,
                FontSize = size,
                Color = rgba,
                Font = font,
                Tile = tile,
                PowerPointOnSlides = pptxSlides,
            };

            var mode = toPdf ? OutputMode.Pdf : OutputMode.Original;
            var format = engine.Detect(input!);
            output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input!))!,
                                    WatermarkEngine.SuggestOutputName(input!, format, mode));

            var result = engine.Process(input!, output, options, mode);

            foreach (var w in result.Warnings) Console.Error.WriteLine($"Warning: {w}");
            Console.WriteLine($"Watermark applied ({result.Format.Description}{(result.ConvertedToPdf ? ", converted to PDF" : "")}): {output}");
            return Ok;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return UsageError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error while processing '{input}': {ex.Message}");
#if DEBUG
            Console.Error.WriteLine(ex);
#endif
            return Failure;
        }
    }

    private static double ParseNumber(string value, string option)
    {
        // Accetta sia "0.3" sia "0,3".
        if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        throw new FormatException($"Invalid number for {option}: '{value}'.");
    }

    private static bool HasExplicitAlpha(string color) => color.Trim().TrimStart('#').Length == 8;

    private static void PrintFormats(WatermarkEngine engine)
    {
        Console.WriteLine("Formats kept as they are:");
        Console.WriteLine("  " + string.Join(" ", engine.NativeExtensions));
        Console.WriteLine();
        if (engine.Converter is null)
        {
            Console.WriteLine("PDF conversion: not available (LibreOffice not found, use --libreoffice <path>).");
        }
        else
        {
            Console.WriteLine($"PDF conversion with --to-pdf ({engine.Converter.Description}):");
            Console.WriteLine("  " + string.Join(" ", engine.PdfExtensions));
        }
        if (!engine.NativeExtensions.Contains(".mp4"))
            Console.WriteLine("\nVideos: not available (FFmpeg not found, use --ffmpeg <path>).");
    }

    private static void PrintHelp(WatermarkEngine? _)
    {
        Console.WriteLine("""
            watermark - applies a text watermark to documents, images, archives and videos

            Usage:
              watermark <file> --text <text> [options]
              watermark --formats

            Options:
              -t, --text <text>      Watermark text (required). Use \n for a new line.
                                     Placeholders: {date} {time} {filename} {user}
              -o, --output <file>    Output file (default: <name>_watermark.<ext>)
              -a, --angle <degrees>  Counterclockwise angle (default: 45)
              -s, --size <points>    Text size in points (default: 60). For images and videos it is
                                     proportional: 60 pt = same proportion as on an A4 page
              -c, --color <color>    #RRGGBB, #RRGGBBAA or a name (red, gray, blue...) (default: #808080)
                  --opacity <0-1>    Opacity; overrides the color's alpha (default: 0.35)
              -f, --font <font>      Font family or path to a .ttf/.otf file (default: Arial)
                  --tile             PDF, XPS, images and videos: repeat the watermark in a grid
                  --pptx-slides      PowerPoint: also place the watermark on top of each slide
                  --to-pdf           Convert to PDF (with LibreOffice), then apply the watermark
                  --libreoffice <p>  Path to soffice, if it is not found automatically
                  --ffmpeg <p>       Path to ffmpeg, if it is not found automatically
                  --formats          List the formats available on this machine
              -h, --help             Show this help

            Examples:
              watermark contract.docx -t "CONFIDENTIAL\n{user} - {date}" -a 30 -s 54 -c "#C00000"
              watermark legacy.doc -t "CONTROLLED COPY" --to-pdf
            """);
    }
}
