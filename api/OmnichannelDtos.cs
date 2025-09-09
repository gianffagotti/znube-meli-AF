using System;
using System.Collections.Generic;

namespace meli_znube_integration.Api
{
    public class OmnichannelResponse
    {
        public OmnichannelData? Data { get; set; }
        public object? Errors { get; set; }
        public string? Status { get; set; }
    }

    public class OmnichannelData
    {
        public int TotalSku { get; set; }
        public List<OmnichannelStockItem> Stock { get; set; } = new List<OmnichannelStockItem>();
        public List<OmnichannelResource> Resources { get; set; } = new List<OmnichannelResource>();
        public Dictionary<string, string> Products { get; set; } = new Dictionary<string, string>();
        public List<OmnichannelVariantType> Variants { get; set; } = new List<OmnichannelVariantType>();
    }

    public class OmnichannelStockItem
    {
        public List<OmnichannelStockDetail> Stock { get; set; } = new List<OmnichannelStockDetail>();
        public List<OmnichannelVariantEntry> Variants { get; set; } = new List<OmnichannelVariantEntry>();
        public string? ProductId { get; set; }
        public string? Sku { get; set; }
    }

    public class OmnichannelStockDetail
    {
        public string ResourceId { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public DateTimeOffset LastUpdateDate { get; set; }
    }

    public class OmnichannelVariantEntry
    {
        public string VariantId { get; set; } = string.Empty;
        public string VariantType { get; set; } = string.Empty;
    }

    public class OmnichannelResource
    {
        public string ResourceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Store { get; set; }
        public double TotalStock { get; set; }
    }

    public class OmnichannelVariantType
    {
        public string TypeName { get; set; } = string.Empty;
        public Dictionary<string, string> Names { get; set; } = new Dictionary<string, string>();
    }
}


