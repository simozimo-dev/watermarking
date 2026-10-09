using System.Globalization;
using System.Security;
using DocumentFormat.OpenXml.Packaging;
using Watermarker.Core.Rendering;
using P = DocumentFormat.OpenXml.Presentation;

namespace Watermarker.Core.Office;

/// <summary>
/// Watermark PowerPoint come casella di testo bloccata.
///
///  - Sempre: su ogni master e su OGNI layout. I layout possono nascondere la grafica del
///    master, per questo la forma viene messa direttamente anche lì. Le slide nuove la
///    ereditano automaticamente e il contenuto delle slide resta del tutto modificabile.
///  - Con --pptx-slides: anche in primo piano su ogni singola slide, così non può essere
///    coperto da immagini a tutta pagina. In questo caso la forma ha il blocco "noSelect";
///    verifica sulla tua versione di PowerPoint che i click passino agli oggetti sottostanti.
///
/// Tutte le forme hanno i blocchi DrawingML (spLocks) su selezione, spostamento, rotazione,
/// ridimensionamento e modifica del testo, e un nome neutro nel riquadro di selezione.
/// </summary>
public sealed class PowerPointWatermarker : IWatermarker
{
    private const long EmuPerPoint = 12700;

    public IReadOnlyCollection<string> Extensions { get; } = [".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"];

    public WatermarkResult Apply(string inputPath, string outputPath, WatermarkOptions options)
    {
        File.Copy(inputPath, outputPath, overwrite: true);
        var warnings = new List<string>();

        using var doc = PresentationDocument.Open(outputPath, isEditable: true);
        var pres = doc.PresentationPart ?? throw new InvalidDataException("Invalid presentation.");

        long slideW = pres.Presentation?.SlideSize?.Cx?.Value ?? 12_192_000;
        long slideH = pres.Presentation?.SlideSize?.Cy?.Value ?? 6_858_000;
        double textW = FontLoader.MeasureWidth(options.Text, options.Font, options.FontSize);
        string family = FontLoader.FamilyName(options.Font);

        var layoutsDone = new HashSet<Uri>();
        foreach (var master in pres.SlideMasterParts)
        {
            AddShape(master.SlideMaster?.CommonSlideData?.ShapeTree, options, textW, family, slideW, slideH, userDrawn: true);
            master.SlideMaster?.Save();

            foreach (var layout in master.SlideLayoutParts)
            {
                if (!layoutsDone.Add(layout.Uri)) continue;
                AddShape(layout.SlideLayout?.CommonSlideData?.ShapeTree, options, textW, family, slideW, slideH, userDrawn: true);
                layout.SlideLayout?.Save();
            }
        }

        int slideNo = 0;
        foreach (var slidePart in GetSlidesInOrder(pres))
        {
            slideNo++;
            var slide = slidePart.Slide;
            if (slide is null) continue;

            if (options.PowerPointOnSlides)
            {
                AddShape(slide.CommonSlideData?.ShapeTree, options, textW, family, slideW, slideH, userDrawn: false);
                slide.Save();
            }
            else if (slide.ShowMasterShapes?.Value == false)
            {
                warnings.Add($"Slide {slideNo}: hides background graphics, so the watermark is not visible. " +
                             "Enable the option to place it on the slides as well.");
            }
        }

        return new WatermarkResult(warnings);
    }

    private static IEnumerable<SlidePart> GetSlidesInOrder(PresentationPart pres)
    {
        var ids = pres.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? Enumerable.Empty<P.SlideId>();
        foreach (var id in ids)
            if (id.RelationshipId?.Value is { } rel && pres.TryGetPartById(rel, out var part) && part is SlidePart sp)
                yield return sp;
    }

    private static void AddShape(P.ShapeTree? tree, WatermarkOptions o, double textWidthPt, string family,
                                 long slideW, long slideH, bool userDrawn)
    {
        if (tree is null) return;

        uint nextId = tree.Descendants<P.NonVisualDrawingProperties>()
            .Select(p => p.Id?.Value ?? 0u)
            .DefaultIfEmpty(1u)
            .Max() + 1;

        long w = (long)((textWidthPt + o.FontSize) * EmuPerPoint);
        var lines = FontLoader.Lines(o.Text);
        long h = (long)(o.FontSize * (1.6 + (lines.Length - 1) * FontLoader.LineSpacing) * EmuPerPoint);
        long x = (slideW - w) / 2;
        long y = (slideH - h) / 2;
        int rot = (int)Math.Round(o.ClockwiseDegrees * 60000) % 21_600_000;
        int size = (int)Math.Clamp(Math.Round(o.FontSize * 100), 100, 400_000);
        int alpha = (int)Math.Round(o.Color.Opacity * 100_000);

        string font = SecurityElement.Escape(family);

        // Un paragrafo per riga, con interlinea fissa uguale agli altri formati.
        int spacing = (int)Math.Round(o.FontSize * FontLoader.LineSpacing * 100);
        string paragraphs = string.Concat(lines.Select(line => $"""
                <a:p>
                  <a:pPr algn="ctr"><a:lnSpc><a:spcPts val="{spacing}"/></a:lnSpc></a:pPr>
                  <a:r>
                    <a:rPr lang="en-US" sz="{size}" b="1" dirty="0">
                      <a:solidFill><a:srgbClr val="{o.Color.RgbHex}"><a:alpha val="{alpha}"/></a:srgbClr></a:solidFill>
                      <a:latin typeface="{font}"/>
                      <a:cs typeface="{font}"/>
                    </a:rPr>
                    <a:t>{SecurityElement.Escape(line)}</a:t>
                  </a:r>
                </a:p>
                """));
        string nvPr = userDrawn ? "<p:nvPr userDrawn=\"1\"/>" : "<p:nvPr/>";

        var xml = $"""
            <p:sp xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:nvSpPr>
                <p:cNvPr id="{nextId}" name="Object {nextId}"/>
                <p:cNvSpPr txBox="1">
                  <a:spLocks noGrp="1" noSelect="1" noRot="1" noChangeAspect="1" noMove="1" noResize="1" noEditPoints="1" noAdjustHandles="1" noChangeArrowheads="1" noChangeShapeType="1" noTextEdit="1"/>
                </p:cNvSpPr>
                {nvPr}
              </p:nvSpPr>
              <p:spPr>
                <a:xfrm rot="{rot}"><a:off x="{x}" y="{y}"/><a:ext cx="{w}" cy="{h}"/></a:xfrm>
                <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                <a:noFill/>
              </p:spPr>
              <p:txBody>
                <a:bodyPr wrap="none" lIns="0" tIns="0" rIns="0" bIns="0" anchor="ctr" rtlCol="0"><a:noAutofit/></a:bodyPr>
                <a:lstStyle/>
                {paragraphs}
              </p:txBody>
            </p:sp>
            """;

        // Ultima forma dell'albero = in primo piano (l'eventuale extLst deve restare in fondo).
        var shape = new P.Shape(xml);
        if (tree.LastChild is P.ExtensionListWithModification ext) ext.InsertBeforeSelf(shape);
        else tree.Append(shape);
    }
}
