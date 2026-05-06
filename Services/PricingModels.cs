namespace Fixlosophy.Services;

public class ServicePricing
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal CurrentPrice { get; set; }
    public string Duration { get; set; } = "";
    public string Icon { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsQuoteOnly { get; set; }
}

public class PriceAdjustment
{
    public int Id { get; set; }
    public int Year { get; set; }
    public decimal Rate { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.Now;
}
