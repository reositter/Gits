using System;
using System.Collections.Generic;

namespace Inventering.AdkBridge
{
    // Samma JSON-format används av webbtjänsten (Inventering.Core.Visma.AdkBridgeProtocol).

    public class BridgeRequest
    {
        public string Command { get; set; }
        /// <summary>Sökväg till företagsmappen, t.ex. C:\ProgramData\SPCS\SPCS Administration\Företag\FTG3.</summary>
        public string CompanyPath { get; set; }
        /// <summary>Sökväg till gemensamma filer (tom = ADK:s standard).</summary>
        public string CommonPath { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public DateTime? Date { get; set; }
        public List<WriteRow> Rows { get; set; }
    }

    public class WriteRow
    {
        public string ArticleNo { get; set; }
        public double Quantity { get; set; }
    }

    public class ArticleDto
    {
        public string ArticleNo { get; set; }
        public string Name { get; set; }
        public string Ean { get; set; }
        public string Unit { get; set; }
        public double StockQuantity { get; set; }
        public bool IsStockItem { get; set; }
        public bool Inactive { get; set; }
    }

    public class WriteResultDto
    {
        public string ArticleNo { get; set; }
        public bool Success { get; set; }
        public double? StockBefore { get; set; }
        public string Message { get; set; }
    }

    public class StatusDto
    {
        public string CompanyName { get; set; }
        public string AdkVersion { get; set; }
        public string Product { get; set; }
        public bool HasInventory { get; set; }
        public int InventoryListRows { get; set; }
        public DateTime? InventoryListDate { get; set; }
    }
}
