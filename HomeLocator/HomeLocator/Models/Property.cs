using System.ComponentModel.DataAnnotations;

namespace HomeLocator.Models;

public class Property
{
    public int Id { get; set; }
    public string? Str { get; set; }
    public string? Hnr { get; set; }
    public string? HnrZus { get; set; }
    public string? Plz { get; set; }
    public string? Ort { get; set; }
    public string? Gemeinde { get; set; }
    public double? FlaecheAmtl { get; set; }
    public int ImportLogId { get; set; }
    public ImportLog ImportLog { get; set; } = null!;
}
