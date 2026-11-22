using System.Collections.Generic;
using System.Xml.Linq;
using EmmcUltra.Models;

namespace EmmcUltra.Services;

public static class PatchParser
{
    public static List<PatchEntry> Parse(string xmlPath)
    {
        var doc = XDocument.Load(xmlPath);
        var list = new List<PatchEntry>();

        foreach (var x in doc.Descendants("patch"))
        {
            string A(string n) => ((string?)x.Attribute(n) ?? "").Trim();

            list.Add(new PatchEntry
            {
                SectorSize = int.TryParse(A("SECTOR_SIZE_IN_BYTES"), out var ss) ? ss : 512,
                ByteOffset = RawProgramParser.EvalSector(A("byte_offset"), 0),
                FileName = A("filename"),
                PhysicalPartition = int.TryParse(A("physical_partition_number"), out var pp) ? pp : 0,
                SizeInBytes = (int)RawProgramParser.EvalSector(A("size_in_bytes"), 0),
                StartSectorExpr = A("start_sector"),
                ValueExpr = A("value"),
                What = A("what"),
            });
        }
        return list;
    }
}