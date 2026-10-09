using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Watermarker.Core.Packaging;

/// <summary>
/// Pacchetto ZIP caricato in memoria (OpenDocument, Visio, XPS): si leggono e sostituiscono
/// le parti XML e poi si riscrive l'archivio mantenendo ordine e date delle voci.
/// Se esiste la voce "mimetype" (OpenDocument) viene scritta per prima e senza compressione,
/// come richiesto dalla specifica.
/// </summary>
internal sealed class ZipPackage
{
    private const long MaxTotalBytes = 1L << 30; // 1 GB non compressi

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry(string name, byte[] data, DateTimeOffset lastWrite)
    {
        public string Name { get; } = name;
        public byte[] Data { get; set; } = data;
        public DateTimeOffset LastWrite { get; } = lastWrite;
    }

    public static ZipPackage Load(string path)
    {
        var pkg = new ZipPackage();
        long total = 0;

        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            using var src = e.Open();
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            total += ms.Length;
            if (total > MaxTotalBytes)
                throw new InvalidDataException("The package exceeds 1 GB when uncompressed.");

            var entry = new Entry(e.FullName, ms.ToArray(), e.LastWriteTime);
            pkg._entries.Add(entry);
            pkg._byName[e.FullName] = entry;
        }
        return pkg;
    }

    public IEnumerable<string> Names => _entries.Select(e => e.Name);

    public bool Contains(string name) => _byName.ContainsKey(name);

    public byte[] Get(string name) =>
        _byName.TryGetValue(name, out var e) ? e.Data : throw new FileNotFoundException($"Part '{name}' not found in the package.");

    public XDocument GetXml(string name)
    {
        using var ms = new MemoryStream(Get(name));
        return XDocument.Load(ms, LoadOptions.PreserveWhitespace);
    }

    public void Set(string name, byte[] data)
    {
        if (_byName.TryGetValue(name, out var e)) e.Data = data;
        else
        {
            var entry = new Entry(name, data, DateTimeOffset.Now);
            _entries.Add(entry);
            _byName[name] = entry;
        }
    }

    public void SetXml(string name, XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(ms, settings)) doc.Save(writer);
        Set(name, ms.ToArray());
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var mimetype = _entries.FirstOrDefault(e => e.Name == "mimetype");
        if (mimetype is not null) Write(zip, mimetype, CompressionLevel.NoCompression);

        foreach (var e in _entries.Where(e => !ReferenceEquals(e, mimetype)))
            Write(zip, e, CompressionLevel.Optimal);
    }

    private static void Write(ZipArchive zip, Entry e, CompressionLevel level)
    {
        var ze = zip.CreateEntry(e.Name, level);
        ze.LastWriteTime = e.LastWrite;
        if (e.Name.EndsWith('/')) return; // cartella
        using var s = ze.Open();
        s.Write(e.Data);
    }

    /// <summary>Risolve un percorso relativo (Target di una relazione) rispetto alla cartella della parte sorgente.</summary>
    public static string ResolveRelative(string sourcePart, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var baseDir = sourcePart.Contains('/') ? sourcePart[..(sourcePart.LastIndexOf('/') + 1)] : "";
        var parts = new List<string>();
        foreach (var seg in (baseDir + target).Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>Percorso del file .rels associato a una parte (visio/pages/pages.xml → visio/pages/_rels/pages.xml.rels).</summary>
    public static string RelsPathFor(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? $"_rels/{part}.rels" : $"{part[..slash]}/_rels/{part[(slash + 1)..]}.rels";
    }
}
