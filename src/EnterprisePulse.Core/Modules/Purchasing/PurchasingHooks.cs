using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Resources;

namespace EnterprisePulse.Core.Modules.Purchasing;

/// <summary>Requesters without purchasing:read only see their own requests. Requests change through the request screen.</summary>
public sealed class PurchaseRequestsHook(AppDbContext db) : ManagedRecordHook("purchase-requests", "Talepler Satın Alma Talebi ekranından yönetilir")
{
    public override IQueryable<T> Scope<T>(IQueryable<T> query)
    {
        if (db.User.Has("purchasing:read") || query is not IQueryable<PurchaseRequest> requests) return query;
        return (IQueryable<T>)requests.Where(r => r.RequestedById == db.User.UserId);
    }
}

public sealed class PurchaseRequestLinesHook() : ManagedRecordHook("purchase-request-lines", "Talep satırları talep formundan yönetilir");

public sealed class SupplierQuotesHook() : ManagedRecordHook("supplier-quotes", "Teklifler Tedarikçi Teklifi ekranından yönetilir");

public sealed class SupplierQuoteLinesHook() : ManagedRecordHook("supplier-quote-lines", "Teklif satırları teklif formundan yönetilir");

public sealed class PurchaseOrdersHook() : ManagedRecordHook("purchase-orders", "Siparişler Satın Alma Siparişi ekranından yönetilir");

public sealed class PurchaseOrderLinesHook() : ManagedRecordHook("purchase-order-lines", "Sipariş satırları sipariş formundan yönetilir");

public sealed class GoodsReceiptsHook() : ManagedRecordHook("goods-receipts", "Mal kabul, siparişin Mal kabul işlemiyle oluşur");

public sealed class GoodsReceiptLinesHook() : ManagedRecordHook("goods-receipt-lines", "Mal kabul satırları değiştirilemez");

public sealed class PurchaseInvoicesHook() : ManagedRecordHook("purchase-invoices", "Alış faturaları fatura ekranından kesilir");

public sealed class PurchaseInvoiceLinesHook() : ManagedRecordHook("purchase-invoice-lines", "Fatura satırları fatura formundan yönetilir");
