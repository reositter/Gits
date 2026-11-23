using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AvSynkLib.Logic;
using AvSynkLib.Logic.AdkClasses;

namespace Inventering.AdkBridge
{
    /// <summary>
    /// Körs av webbtjänsten: läser en BridgeRequest (JSON) från stdin, gör jobbet via ADK,
    /// skriver resultat (JSON) till stdout. Exit 0 = ok, 1 = fel ({"error": "..."}).
    /// Lösenord skickas via stdin, inte som argument (syns annars i processlistan).
    /// </summary>
    internal static class Program
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
            try
            {
                var input = Console.In.ReadToEnd();
                var req = JsonSerializer.Deserialize<BridgeRequest>(input, Json)
                          ?? throw new InvalidOperationException("Tom begäran.");
                object result;
                // Bolagslistan kräver inget öppnat bolag.
                if (string.Equals(req.Command, "companies", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Out.Write(JsonSerializer.Serialize(Companies(req), Json));
                    return 0;
                }
                using (var company = Open(req))
                {
                    switch ((req.Command ?? "").ToLowerInvariant())
                    {
                        case "status": result = Status(req.CompanyPath); break;
                        case "articles": result = Articles(); break;
                        case "inventory-write": result = InventoryWrite(req); break;
                        default: throw new InvalidOperationException("Okänt kommando: " + req.Command);
                    }
                    company.CloseCompany();
                }
                Console.Out.Write(JsonSerializer.Serialize(result, Json));
                return 0;
            }
            catch (AvException ex)
            {
                return Fail($"ADK-fel {ex.ErrorCode}: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Fail(ex.GetBaseException().Message);
            }
        }

        private static int Fail(string message)
        {
            Console.Out.Write(JsonSerializer.Serialize(new { error = message }, Json));
            return 1;
        }

        private static AvCompany Open(BridgeRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CompanyPath)) throw new InvalidOperationException("Företagssökväg saknas (Visma:Adk:CompanyPath).");
            if (!Directory.Exists(req.CompanyPath)) throw new DirectoryNotFoundException("Företagsmappen finns inte: " + req.CompanyPath);
            return new AvCompany(req.CompanyPath, req.CommonPath ?? "", req.User ?? "", req.Password ?? "");
        }

        private static List<CompanyDto> Companies(BridgeRequest req)
        {
            var list = string.IsNullOrWhiteSpace(req.CommonPath)
                ? AvCompany.GetAvailableCompanies()
                : AvCompany.GetAvailableCompanies(req.CommonPath, 0);
            return list.Select(c => new CompanyDto
            {
                Name = c.Name, Directory = c.Directory, DirectoryFullPath = c.DirectoryFullPath, DatabaseName = c.DatabaseName,
            }).ToList();
        }

        private static StatusDto Status(string companyPath)
        {
            var s = new StatusDto
            {
                AdkVersion = Safe(() => AvCompany.AdkVersion.ToString()),
                Product = AvCompany.IsAdmin2000 ? "Visma Administration 2000" : AvCompany.IsAdmin1000 ? "Visma Administration 1000" : AvCompany.IsAdmin500 ? "Visma Administration 500" : "Okänd",
                HasInventory = AvCompany.HasInventory,
                CompanyName = companyPath,
            };
            if (s.HasInventory)
            {
                using (var inv = new AvInventoryArticle())
                {
                    if (inv.MoveFirst())
                    {
                        s.InventoryListDate = inv.Date == DateTime.MinValue ? (DateTime?)null : inv.Date;
                        while (!inv.Eof) { s.InventoryListRows++; inv.MoveNext(); }
                    }
                }
            }
            return s;
        }

        private static List<ArticleDto> Articles()
        {
            var list = new List<ArticleDto>();
            using (var a = new AvArticle { Sort = SortOrderInfo.SortOrder.Article_ArticleNumber, FastMove = true })
            {
                if (!a.MoveFirst()) return list;
                while (!a.Eof)
                {
                    list.Add(new ArticleDto
                    {
                        ArticleNo = (a.Number ?? "").Trim(),
                        Name = (a.Name ?? "").Trim(),
                        Ean = string.IsNullOrWhiteSpace(a.BarCode) ? null : a.BarCode.Trim(),
                        Unit = a.UnitCode,
                        StockQuantity = a.QuantityInStock,
                        IsStockItem = a.StockGoods,
                        Inactive = a.Inactive,
                    });
                    a.MoveNext();
                }
            }
            return list;
        }

        /// <summary>
        /// Skriver räknat antal i Vismas inventeringsunderlag (en rad per artikel).
        /// Underlaget måste vara skapat i Visma innan. Lagret uppdateras först när
        /// användaren slutför inventeringen i Visma – det är medvetet ett sista godkännandesteg.
        /// </summary>
        private static List<WriteResultDto> InventoryWrite(BridgeRequest req)
        {
            var rows = (req.Rows ?? new List<WriteRow>())
                .GroupBy(r => (r.ArticleNo ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            var date = (req.Date ?? DateTime.Today).Date;
            var results = new Dictionary<string, WriteResultDto>(StringComparer.OrdinalIgnoreCase);

            using (var inv = new AvInventoryArticle())
            {
                if (inv.MoveFirst())
                {
                    while (!inv.Eof)
                    {
                        var no = (inv.ArticleNumber ?? "").Trim();
                        if (rows.TryGetValue(no, out var row) && !results.ContainsKey(no))
                        {
                            var r = new WriteResultDto { ArticleNo = no, StockBefore = inv.QuantityInStock };
                            try
                            {
                                inv.Quantity = row.Quantity;
                                inv.Date = date;
                                inv.Update();
                                r.Success = true;
                            }
                            catch (Exception ex)
                            {
                                r.Message = ex.GetBaseException().Message;
                            }
                            results[no] = r;
                        }
                        inv.MoveNext();
                    }
                }
            }

            return rows.Keys.Select(no => results.TryGetValue(no, out var r) ? r : new WriteResultDto
            {
                ArticleNo = no,
                Success = false,
                Message = "Artikeln finns inte i inventeringsunderlaget i Visma. Skapa/uppdatera underlaget i Visma och skicka igen.",
            }).ToList();
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
